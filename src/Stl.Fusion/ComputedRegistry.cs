using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Stl.Concurrency;
using Stl.Locking;
using Stl.Mathematics;
using Stl.OS;
using Stl.Time;
using Stl.Time.Internal;
using Errors = Stl.Fusion.Internal.Errors;

namespace Stl.Fusion
{
    public class ComputedRegistry : IDisposable
    {
        public static ComputedRegistry Instance { get; set; } = new ComputedRegistry();

        public sealed class Options
        {
            internal static PrimeSieve CapacityPrimeSieve;
            public static int DefaultInitialCapacity { get; }

            public int InitialCapacity { get; set; } = DefaultInitialCapacity;
            public int ConcurrencyLevel { get; set; } = HardwareInfo.ProcessorCount;
            public Func<IFunction, IAsyncLockSet<ComputedInput>>? LocksProvider { get; set; } = null;
            public GCHandlePool? GCHandlePool { get; set; } = null;
            public IMomentClock Clock { get; set; } = CoarseCpuClock.Instance;

            static Options()
            {
                var capacity = Math.Min(16_384, HardwareInfo.ProcessorCountPo2 * 128);
                CapacityPrimeSieve = new PrimeSieve(capacity + 1024);
                while (!CapacityPrimeSieve.IsPrime(capacity))
                    capacity--;
                DefaultInitialCapacity = capacity;
                // Debug.WriteLine($"{nameof(ComputedRegistry)}.{nameof(Options)}.{nameof(DefaultInitialCapacity)} = {DefaultInitialCapacity}");
            }
        }

        private readonly ConcurrentDictionary<ComputedInput, Entry> _storage;
        private readonly Func<IFunction, IAsyncLockSet<ComputedInput>> _locksProvider;
        private readonly GCHandlePool _gcHandlePool;
        private readonly StochasticCounter _opCounter;
        private readonly IMomentClock _clock;
        private volatile int _pruneCounterThreshold;
        private Task? _pruneTask;
        private object Lock => _storage;

        public ComputedRegistry(Options? options = null)
        {
            options ??= new Options();
            _storage = new ConcurrentDictionary<ComputedInput, Entry>(options.ConcurrencyLevel, options.InitialCapacity);
            var locksProvider = options.LocksProvider;
            if (locksProvider == null) {
                var locks = new AsyncLockSet<ComputedInput>(ReentryMode.CheckedFail);
                locksProvider = _ => locks;
            }
            _locksProvider = locksProvider;
            _gcHandlePool = options.GCHandlePool ?? new GCHandlePool(GCHandleType.Weak);
            if (_gcHandlePool.HandleType != GCHandleType.Weak)
                throw new ArgumentOutOfRangeException(
                    $"{nameof(options)}.{nameof(options.GCHandlePool)}.{nameof(_gcHandlePool.HandleType)}");
            _opCounter = new StochasticCounter();
            _clock = options.Clock;
            UpdatePruneCounterThreshold();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
            => _gcHandlePool.Dispose();

        public virtual IComputed? TryGet(ComputedInput key)
        {
            var random = Randomize(key.HashCode);
            OnOperation(random);
            if (_storage.TryGetValue(key, out var entry)) {
                var value = entry.Computed;
                if (value != null) {
                    value.Touch();
                    return value;
                }

                var handle = entry.Handle;
                value = (IComputed?) handle.Target;
                if (value != null) {
                    value.Touch();
                    _storage.TryUpdate(key, new Entry(value, handle), entry);
                    return value;
                }
                if (_storage.TryRemove(key, entry))
                    _gcHandlePool.Release(handle, random);
            }
            return null;
        }

        public virtual void Register(IComputed computed)
        {
            // Debug.WriteLine($"{nameof(Register)}: {computed}");
            var key = computed.Input;
            var random = Randomize(key.HashCode);
            OnOperation(random);

            var spinWait = new SpinWait();
            Entry? newEntry = null;
            while (computed.State != ComputedState.Invalidated) {
                if (_storage.TryGetValue(key, out var entry)) {
                    var handle = entry.Handle;
                    var target = (IComputed?) handle.Target;
                    if (target == computed)
                        break;
                    if (target == null || target.State == ComputedState.Invalidated) {
                        if (_storage.TryRemove(key, entry))
                            _gcHandlePool.Release(handle, random);
                    }
                    else {
                        // This typically triggers Unregister -
                        // except for ReplicaClientComputed.
                        target.Invalidate();
                    }
                }
                else {
                    newEntry ??= new Entry(computed, _gcHandlePool.Acquire(computed, random));
                    if (_storage.TryAdd(key, newEntry.GetValueOrDefault())) {
                        if (computed.State == ComputedState.Invalidated) {
                            if (_storage.TryRemove(key, entry))
                                _gcHandlePool.Release(entry.Handle, random);
                        }
                        break;
                    }
                }
                spinWait.SpinOnce();
            }
        }

        public virtual bool Unregister(IComputed computed)
        {
            // Debug.WriteLine($"{nameof(Unregister)}: {computed}");
            // We can't remove what still could be invalidated,
            // since "usedBy" links are resolved via this registry
            if (computed.State != ComputedState.Invalidated)
                throw Errors.WrongComputedState(computed.State);

            var key = computed.Input;
            var random = Randomize(key.HashCode);
            OnOperation(random);
            if (!_storage.TryGetValue(key, out var entry))
                return false;
            var handle = entry.Handle;
            var target = handle.Target;
            if (target != null && !ReferenceEquals(target, computed))
                return false;
            // gcHandle.Target == null (is gone, i.e. to be pruned)
            // or pointing to the right computation object
            if (!_storage.TryRemove(key, entry))
                // If another thread removed the entry, it also released the handle
                return false;
            _gcHandlePool.Release(handle, random);
            return true;
        }

        public virtual IAsyncLockSet<ComputedInput> GetLocksFor(IFunction function)
            => _locksProvider.Invoke(function);

        // Private members

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Randomize(int random)
            => random + CoarseStopwatch.RandomInt32;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnOperation(int random)
        {
            if (!_opCounter.Increment(random, out var opCounterValue))
                return;
            if (opCounterValue > _pruneCounterThreshold)
                TryPrune();
        }

        private void TryPrune()
        {
            lock (Lock) {
                // Double check locking
                if (_opCounter.ApproximateValue <= _pruneCounterThreshold)
                    return;
                _opCounter.ApproximateValue = 0;
                Prune();
            }
        }

        private void Prune()
        {
            lock (Lock) {
                if (_pruneTask == null || _pruneTask.IsCompleted)
                    _pruneTask = Task.Run(PruneInternal);
            }
        }

        private void PruneInternal()
        {
            // Debug.WriteLine(nameof(PruneInternal));
            var now = _clock.Now;
            var randomOffset = Randomize(Thread.CurrentThread.ManagedThreadId);
            foreach (var (key, entry) in _storage) {
                var handle = entry.Handle;
                if (handle.Target == null) {
                    if (_storage.TryRemove(key, entry)) {
                        var random = key.HashCode + randomOffset;
                        _gcHandlePool.Release(handle, random);
                    }
                    continue;
                }
                var computed = entry.Computed;
                if (computed == null)
                    continue;
                var expirationTime = computed.LastAccessTime + computed.Options.KeepAliveTime;
                if (expirationTime >= now)
                    continue;
                _storage.TryUpdate(key, new Entry(null, handle), entry);
            }

            lock (Lock) {
                UpdatePruneCounterThreshold();
                _opCounter.ApproximateValue = 0;
            }
        }

        private void UpdatePruneCounterThreshold()
        {
            lock (Lock) {
                // Should be called inside Lock
                var capacity = (long) _storage.GetCapacity();
                var nextThreshold = (int) Math.Min(int.MaxValue >> 1, capacity);
                _pruneCounterThreshold = nextThreshold;
            }
        }

        private readonly struct Entry : IEquatable<Entry>
        {
            public readonly IComputed? Computed;
            public readonly GCHandle Handle;

            public Entry(IComputed? computed, GCHandle handle)
            {
                Computed = computed;
                Handle = handle;
            }

            public bool Equals(Entry other)
                => Computed == other.Computed && Handle == other.Handle;
            public override bool Equals(object? obj)
                => obj is Entry other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Computed, Handle);
            public static bool operator ==(Entry left, Entry right) => left.Equals(right);
            public static bool operator !=(Entry left, Entry right) => !left.Equals(right);
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Stl.Generators;

namespace Stl.Fusion
{
    public class SimpleComputed<T> : Computed<SimpleComputedInput, T>
    {
        public new SimpleComputedInput<T> Input => (SimpleComputedInput<T>) base.Input;

        protected internal SimpleComputed(ComputedOptions options, SimpleComputedInput input, LTag version)
            : base(options, input, version) { }
        protected internal SimpleComputed(ComputedOptions options, SimpleComputedInput input,
            Result<T> output, LTag version, bool isConsistent = true)
            : base(options, input, output, version, isConsistent) { }
    }

    public static class SimpleComputed
    {
        // Task versions

        public static IComputed<T> New<T>(
            Func<IComputed<T>, IComputed<T>, CancellationToken, Task> updater)
            => New(ComputedOptions.Default, updater, default, false);
        public static IComputed<T> New<T>(
            ComputedOptions options,
            Func<IComputed<T>, IComputed<T>, CancellationToken, Task> updater)
            => New(options, updater, default, false);

        public static IComputed<T> New<T>(
            Func<IComputed<T>, IComputed<T>, CancellationToken, Task> updater,
            Result<T> output, bool isConsistent = true)
            => New(ComputedOptions.Default, updater, output, isConsistent);
        public static IComputed<T> New<T>(
            ComputedOptions options,
            Func<IComputed<T>, IComputed<T>, CancellationToken, Task> updater,
            Result<T> output, bool isConsistent = true)
        {
            var input = new SimpleComputedInput<T>(updater);
            var version = ConcurrentLTagGenerator.Default.Next();
            input.Computed = new SimpleComputed<T>(options, input, output, version, isConsistent);
            return input.Computed;
        }

        // Task<T> versions

        public static IComputed<T> New<T>(
            Func<IComputed<T>, CancellationToken, Task<T>> updater)
            => New(ComputedOptions.Default, Wrap(updater), default, false);
        public static IComputed<T> New<T>(
            ComputedOptions options,
            Func<IComputed<T>, CancellationToken, Task<T>> updater)
            => New(options, Wrap(updater), default, false);

        public static IComputed<T> New<T>(
            Func<IComputed<T>, CancellationToken, Task<T>> updater,
            Result<T> output, bool isConsistent = true)
            => New(ComputedOptions.Default, Wrap(updater), output, isConsistent);
        public static IComputed<T> New<T>(
            ComputedOptions options,
            Func<IComputed<T>, CancellationToken, Task<T>> updater,
            Result<T> output, bool isConsistent = true)
            => New(options, Wrap(updater), output, isConsistent);

        // Private methods

        private static Func<IComputed<T>, IComputed<T>, CancellationToken, Task> Wrap<T>(
            Func<IComputed<T>, CancellationToken, Task<T>> updater)
            => async (prevComputed, nextComputed, cancellationToken) => {
                var value = await updater.Invoke(prevComputed, cancellationToken);
                nextComputed.TrySetOutput(new Result<T>(value, null));
            };
    }
}

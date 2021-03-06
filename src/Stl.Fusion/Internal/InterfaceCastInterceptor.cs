using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Castle.DynamicProxy;
using Stl.Concurrency;
using Stl.Extensibility;

namespace Stl.Fusion.Internal
{
    public class InterfaceCastInterceptor : IInterceptor
    {
        private readonly Func<MethodInfo, IInvocation, Action<IInvocation>?> _createHandler;
        private readonly ConcurrentDictionary<MethodInfo, Action<IInvocation>?> _handlerCache =
            new ConcurrentDictionary<MethodInfo, Action<IInvocation>?>();
        private readonly MethodInfo _createConvertingHandlerMethod;

        public InterfaceCastInterceptor()
        {
            _createHandler = CreateHandler;
            _createConvertingHandlerMethod = GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(m => m.Name == nameof(CreateConvertingHandler));
        }

        public void Intercept(IInvocation invocation)
        {
            var handler = _handlerCache.GetOrAddChecked(invocation.Method, _createHandler, invocation);
            if (handler == null)
                invocation.Proceed();
            else
                handler.Invoke(invocation);
        }

        protected virtual Action<IInvocation>? CreateHandler(MethodInfo methodInfo, IInvocation initialInvocation)
        {
            var tProxy = initialInvocation.Proxy.GetType();
            var tTarget = initialInvocation.TargetType;
            var mSource = initialInvocation.Method;
            var mArgTypes = mSource.GetParameters().Select(p => p.ParameterType).ToArray();
            var mTarget = tTarget.GetMethod(mSource.Name, mArgTypes);
            var fTarget = tProxy.GetField("__target", BindingFlags.Instance | BindingFlags.NonPublic);

            Type? TryGetTaskOfTArgument(Type t) {
                if (!t.IsGenericType)
                    return null;
                var tg = t.GetGenericTypeDefinition();
                if (tg != typeof(Task<>))
                    return null;
                return t.GetGenericArguments()[0];
            }

            if (mTarget.ReturnType != mSource.ReturnType) {
                var rtSource = TryGetTaskOfTArgument(mSource.ReturnType);
                var rtTarget = TryGetTaskOfTArgument(mTarget.ReturnType);
                if (rtSource != null && rtTarget != null) {
                    var result = (Action<IInvocation>?) _createConvertingHandlerMethod
                        .MakeGenericMethod(rtSource, rtTarget)
                        .Invoke(this, new object[] {initialInvocation, fTarget, mTarget});
                    if (result != null)
                        return result;
                }
            }

            return invocation => {
                // TODO: Get rid of reflection here (not critical)
                var target = fTarget.GetValue(invocation.Proxy);
                invocation.ReturnValue = mTarget.Invoke(target, invocation.Arguments);
            };
        }

        protected virtual Action<IInvocation>? CreateConvertingHandler<TSource, TTarget>(
            IInvocation initialInvocation, FieldInfo fTarget, MethodInfo mTarget)
        {
            var tSource = typeof(TSource);
            var tTarget = typeof(TTarget);

            // Fast conversion via IConvertibleTo<T>
            if (typeof(IConvertibleTo<>).MakeGenericType(tSource).IsAssignableFrom(tTarget)) {
                return invocation => {
                    var target = fTarget.GetValue(invocation.Proxy);
                    var untypedResult = mTarget.Invoke(target, invocation.Arguments);
                    var result = (Task<TTarget>) untypedResult;
                    invocation.ReturnValue = result.ContinueWith(t =>
                        t.Result is IConvertibleTo<TSource> c
                            ? c.Convert()
                            : default!);
                };
            }

            // Slow conversion via TypeConverter(s)
            var d = TypeDescriptor.GetConverter(tTarget);
            if (!d.CanConvertTo(tSource))
                return null;

            return invocation => {
                // TODO: Get rid of reflection here (not critical)
                var target = fTarget.GetValue(invocation.Proxy);
                var untypedResult = mTarget.Invoke(target, invocation.Arguments);
                var result = (Task<TTarget>) untypedResult;
                invocation.ReturnValue = result.ContinueWith(t =>
                    (TSource) d.ConvertTo(t.Result, tSource));
            };
        }
    }
}

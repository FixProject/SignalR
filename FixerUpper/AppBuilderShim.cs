using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Owin;
using Microsoft.Owin.Infrastructure;
using Owin;

namespace FixerUpper
{
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using AppFunc = Func<IDictionary<string,object>, Task>;

    class AppBuilderShim : IAppBuilder
    {
        private static readonly object NotFoundSignal = new object();
        private readonly Action<Func<Func<IDictionary<string, object>, Task>, Func<IDictionary<string, object>, Task>>> _use;
        private readonly Dictionary<string, object> _properties;
        private readonly Dictionary<Tuple<Type, Type>, Delegate> _conversions;

        public AppBuilderShim(Action<Func<Func<IDictionary<string, object>, Task>, Func<IDictionary<string, object>, Task>>> use)
        {
            _use = use;
            _properties = new Dictionary<string, object>();
            _conversions = new Dictionary<Tuple<Type, Type>, Delegate>();

            _properties[Constants.BuilderAddConversion] = new Action<Delegate>(AddSignatureConversion);
            _properties[Constants.BuilderDefaultApp] = (AppFunc)NotFound;

            SignatureConversions.AddConversions(this);

        }

        private static Task NotFound(IDictionary<string, object> env)
        {
            env["owin.ResponseStatusCode"] = NotFoundSignal;
            return Task.FromResult(0);
        }

        public IAppBuilder Use(object middlewareObject, params object[] args)
        {
            var middleware = ToMiddlewareFactory(middlewareObject, args);
            Type neededSignature = middleware.Item1;
            Delegate middlewareDelegate = middleware.Item2;
            object[] middlewareArgs = middleware.Item3;


            _use(next =>
            {
                object app = (AppFunc)NotFound;
                app = Convert(neededSignature, app);
                var invokeParameters = new[] { app }.Concat(middlewareArgs).ToArray();
                app = middlewareDelegate.DynamicInvoke(invokeParameters);
                app = Convert(neededSignature, app);
                var appFunc = (AppFunc)Convert(typeof (AppFunc), app);
                return async env =>
                {
                    //var holdResponse = (Stream)env["owin.ResponseBody"];
                    //MemoryStream proxyResponse;
                    //env["owin.ResponseBody"] = proxyResponse = new MemoryStream();
                    try
                    {
                        await appFunc(env);
                        object statusCode;
                        if ((!env.TryGetValue("owin.ResponseStatusCode", out statusCode))
                            || !(ReferenceEquals(statusCode, NotFoundSignal) || (int) statusCode == 400))
                        {
                            //proxyResponse.Position = 0;
                            //await proxyResponse.CopyToAsync(holdResponse);
                            env["owin.ResponseStatusCode"] = 200;
                            return;
                        }
                        //    Trace.TraceInformation("Hm.");
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError(ex.ToString());
                    }
                    //finally
                    //{
                    //    env["owin.ResponseBody"] = holdResponse;
                    //}
                    await next(env);
                };
            });
            return this;
        }

        public object Build(Type returnType)
        {
            return null;
        }

        public IAppBuilder New()
        {
            return new AppBuilderShim(_use);
        }

        public IDictionary<string, object> Properties {
            get { return _properties; }
        }
        private void AddSignatureConversion(Delegate conversion)
        {
            if (conversion == null)
            {
                throw new ArgumentNullException("conversion");
            }

            Type parameterType = GetParameterType(conversion);
            if (parameterType == null)
            {
                throw new ArgumentException("Conversion takes one parameter", "conversion");
            }
            Tuple<Type, Type> key = Tuple.Create(conversion.Method.ReturnType, parameterType);
            _conversions[key] = conversion;
        }

        private static Type GetParameterType(Delegate function)
        {
            ParameterInfo[] parameters = function.Method.GetParameters();
            return parameters.Length == 1 ? parameters[0].ParameterType : null;
        }
        private object Convert(Type signature, object app)
        {
            if (app == null)
            {
                return null;
            }

            object oneHop = ConvertOneHop(signature, app);
            if (oneHop != null)
            {
                return oneHop;
            }

            object multiHop = ConvertMultiHop(signature, app);
            if (multiHop != null)
            {
                return multiHop;
            }
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, "No conversion exists from {0} to {1}", app.GetType(), signature),
                "signature");
        }

        private object ConvertMultiHop(Type signature, object app)
        {
            return (from conversion in _conversions
                let preConversion = ConvertOneHop(conversion.Key.Item2, app)
                where preConversion != null
                select conversion.Value.DynamicInvoke(preConversion)
                into intermediate
                where intermediate != null
                select ConvertOneHop(signature, intermediate)).FirstOrDefault(postConversion => postConversion != null);
        }

        private object ConvertOneHop(Type signature, object app)
        {
            if (signature.IsInstanceOfType(app))
            {
                return app;
            }
            if (typeof(Delegate).IsAssignableFrom(signature))
            {
                Delegate memberDelegate = ToMemberDelegate(signature, app);
                if (memberDelegate != null)
                {
                    return memberDelegate;
                }
            }
            return (from conversion in _conversions
                let returnType = conversion.Key.Item1
                let parameterType = conversion.Key.Item2
                where parameterType.IsInstanceOfType(app) && signature.IsAssignableFrom(returnType)
                select conversion.Value.DynamicInvoke(app)).FirstOrDefault();
        }

        private static Delegate ToMemberDelegate(Type signature, object app)
        {
            MethodInfo signatureMethod = signature.GetMethod(Constants.Invoke);
            ParameterInfo[] signatureParameters = signatureMethod.GetParameters();

            MethodInfo[] methods = app.GetType().GetMethods();
            foreach (var method in methods)
            {
                if (method.Name != Constants.Invoke)
                {
                    continue;
                }
                ParameterInfo[] methodParameters = method.GetParameters();
                if (methodParameters.Length != signatureParameters.Length)
                {
                    continue;
                }
                if (methodParameters
                    .Zip(signatureParameters, (methodParameter, signatureParameter) => methodParameter.ParameterType.IsAssignableFrom(signatureParameter.ParameterType))
                    .Any(compatible => compatible == false))
                {
                    continue;
                }
                if (!signatureMethod.ReturnType.IsAssignableFrom(method.ReturnType))
                {
                    continue;
                }
                return Delegate.CreateDelegate(signature, app, method);
            }
            return null;
        }

        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "False positive")]
        private static Tuple<Type, Delegate, object[]> ToMiddlewareFactory(object middlewareObject, object[] args)
        {
            if (middlewareObject == null)
            {
                throw new ArgumentNullException("middlewareObject");
            }

            var middlewareDelegate = middlewareObject as Delegate;
            if (middlewareDelegate != null)
            {
                return Tuple.Create(GetParameterType(middlewareDelegate), middlewareDelegate, args);
            }

            Tuple<Type, Delegate, object[]> factory = ToInstanceMiddlewareFactory(middlewareObject, args);
            if (factory != null)
            {
                return factory;
            }

            factory = ToGeneratorMiddlewareFactory(middlewareObject, args);
            if (factory != null)
            {
                return factory;
            }

            if (middlewareObject is Type)
            {
                return ToConstructorMiddlewareFactory(middlewareObject, args, ref middlewareDelegate);
            }

            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture,
                "Middleware not supported: '{0}'", middlewareObject.GetType().FullName));
        }

        // Instance pattern: public void Initialize(AppFunc next, string arg1, string arg2), public Task Invoke(IDictionary<...> env)
        private static Tuple<Type, Delegate, object[]> ToInstanceMiddlewareFactory(object middlewareObject, object[] args)
        {
            MethodInfo[] methods = middlewareObject.GetType().GetMethods();
            foreach (var method in methods)
            {
                if (method.Name != Constants.Initialize)
                {
                    continue;
                }
                ParameterInfo[] parameters = method.GetParameters();
                Type[] parameterTypes = parameters.Select(p => p.ParameterType).ToArray();

                if (parameterTypes.Length != args.Length + 1)
                {
                    continue;
                }
                if (!parameterTypes
                    .Skip(1)
                    .Zip(args, TestArgForParameter)
                    .All(x => x))
                {
                    continue;
                }

                // DynamicInvoke can't handle a middleware with multiple args, just push the args in via closure.
                Func<object, object> func = app =>
                {
                    object[] invokeParameters = new[] { app }.Concat(args).ToArray();
                    method.Invoke(middlewareObject, invokeParameters);
                    return middlewareObject;
                };

                return Tuple.Create<Type, Delegate, object[]>(parameters[0].ParameterType, func, new object[0]);
            }
            return null;
        }

        // Delegate nesting pattern: public AppFunc Invoke(AppFunc app, string arg1, string arg2)
        private static Tuple<Type, Delegate, object[]> ToGeneratorMiddlewareFactory(object middlewareObject, object[] args)
        {
            MethodInfo[] methods = middlewareObject.GetType().GetMethods();
            foreach (var method in methods)
            {
                if (method.Name != Constants.Invoke)
                {
                    continue;
                }
                ParameterInfo[] parameters = method.GetParameters();
                Type[] parameterTypes = parameters.Select(p => p.ParameterType).ToArray();

                if (parameterTypes.Length != args.Length + 1)
                {
                    continue;
                }
                if (!parameterTypes
                    .Skip(1)
                    .Zip(args, TestArgForParameter)
                    .All(x => x))
                {
                    continue;
                }
                IEnumerable<Type> genericFuncTypes = parameterTypes.Concat(new[] { method.ReturnType });
                Type funcType = Expression.GetFuncType(genericFuncTypes.ToArray());
                Delegate middlewareDelegate = Delegate.CreateDelegate(funcType, middlewareObject, method);
                return Tuple.Create(parameters[0].ParameterType, middlewareDelegate, args);
            }
            return null;
        }

        // Type Constructor pattern: public Delta(AppFunc app, string arg1, string arg2)
        private static Tuple<Type, Delegate, object[]> ToConstructorMiddlewareFactory(object middlewareObject, object[] args, ref Delegate middlewareDelegate)
        {
            var middlewareType = middlewareObject as Type;
            ConstructorInfo[] constructors = middlewareType.GetConstructors();
            foreach (var constructor in constructors)
            {
                ParameterInfo[] parameters = constructor.GetParameters();
                Type[] parameterTypes = parameters.Select(p => p.ParameterType).ToArray();
                if (parameterTypes.Length != args.Length + 1)
                {
                    continue;
                }
                if (!parameterTypes
                    .Skip(1)
                    .Zip(args, TestArgForParameter)
                    .All(x => x))
                {
                    continue;
                }

                ParameterExpression[] parameterExpressions = parameters.Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                NewExpression callConstructor = Expression.New(constructor, parameterExpressions);
                middlewareDelegate = Expression.Lambda(callConstructor, parameterExpressions).Compile();
                return Tuple.Create(parameters[0].ParameterType, middlewareDelegate, args);
            }

            throw new MissingMethodException(string.Format(CultureInfo.CurrentCulture,
                "No constructor found for {0} with {1} args", middlewareType.FullName, args.Length + 1));
        }

        private static bool TestArgForParameter(Type parameterType, object arg)
        {
            return (arg == null && !parameterType.IsValueType) ||
                parameterType.IsInstanceOfType(arg);
        }
    }
}
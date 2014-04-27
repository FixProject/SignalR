namespace FixerUpper
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNet.SignalR;
    using Microsoft.AspNet.SignalR.Hosting;
    using Microsoft.AspNet.SignalR.Hubs;
    using Microsoft.AspNet.SignalR.Infrastructure;
    using Microsoft.AspNet.SignalR.Owin.Middleware;
    using Microsoft.AspNet.SignalR.Tracing;
    using Microsoft.Owin;
    using Microsoft.Owin.Extensions;
    using Microsoft.Owin.Infrastructure;
    using Microsoft.Owin.Security.DataProtection;
    using Owin;

    public static class SignalRShim
    {
        public static Func<IDictionary<string,object>, Task> Hub()
        {
            return Hub(new Dictionary<string, object>());
        }

        public static Func<IDictionary<string,object>, Task> Hub(IDictionary<string,object> startupEnv)
        {
            return Hub(new HubConfiguration(), startupEnv);
        }

        private static Task NotFound(IDictionary<string, object> env)
        {
            env["owin.ResponseStatusCode"] = 404;
            return Task.FromResult(0);
        }

        public static Func<IDictionary<string,object>, Task> Hub(HubConfiguration configuration, IDictionary<string,object> startupEnv)
        {
            if (configuration == null)
            {
                throw new ArgumentException("No configuration provided");
            }

            var resolver = configuration.Resolver;

            if (resolver == null)
            {
                throw new ArgumentException("No dependency resolver provider");
            }

            var token = startupEnv.GetValueOrDefault("owin.CallCancelled", CancellationToken.None);

            // If we don't get a valid instance name then generate a random one
            string instanceName = startupEnv.GetValueOrDefault("host.AppName", Guid.NewGuid().ToString());

            // Use the data protection provider from app builder and fallback to the
            // Dpapi provider

            var protectedData = new DefaultProtectedData();

            resolver.Register(typeof (IProtectedData), () => protectedData);

            // If the host provides trace output then add a default trace listener
            var traceOutput = startupEnv.GetValueOrDefault("host.TraceOutput", (TextWriter) null);
            if (traceOutput != null)
            {
                var hostTraceListener = new TextWriterTraceListener(traceOutput);
                var traceManager = new TraceManager(hostTraceListener);
                resolver.Register(typeof (ITraceManager), () => traceManager);
            }

            // Try to get the list of reference assemblies from the host
            IEnumerable<Assembly> referenceAssemblies = startupEnv.GetValueOrDefault("host.ReferencedAssemblies",
                (IEnumerable<Assembly>) null);
            if (referenceAssemblies != null)
            {
                // Use this list as the assembly locator
                var assemblyLocator = new EnumerableOfAssemblyLocator(referenceAssemblies);
                resolver.Register(typeof (IAssemblyLocator), () => assemblyLocator);
            }

            resolver.InitializeHost(instanceName, token);

            var hub = new HubDispatcherMiddleware(new KatanaShim(NotFound), configuration);

            return async env =>
            {
                await hub.Invoke(new OwinContext(env));
                if (!env.ContainsKey("owin.ResponseStatusCode"))
                {
                    env["owin.ResponseStatusCode"] = 200;
                }
            };
        }

        class KatanaShim : OwinMiddleware
        {
            private readonly Func<IDictionary<string, object>, Task> _appFunc;

            public KatanaShim(Func<IDictionary<string,object>, Task> appFunc) : base(null)
            {
                _appFunc = appFunc;
            }

            public override Task Invoke(IOwinContext context)
            {
                return _appFunc(context.Environment);
            }
        }
    }
}
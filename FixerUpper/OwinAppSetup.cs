using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.Owin;
using Owin;
using Simple.Owin.Static;

namespace FixerUpper
{
    using AppFunc = Func<IDictionary<string,object>, Task>;
    public class OwinAppSetup
    {
        public static void Setup(Action<Func<AppFunc, AppFunc>> use)
        {
            var appBuilder = new AppBuilderShim(use);
            use(Statics.AddFileAlias("/index.html", "/")
                .AddFolder("/Scripts"));
            appBuilder.MapSignalR();
        }
    }

    public class GameHub : Hub
    {
        public void Play(string c1, string c2)
        {
            Clients.Caller.reply(42);
        }
    }

    internal sealed class AppFuncTransition : OwinMiddleware
    {
        private readonly Func<IDictionary<string, object>, Task> _next;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="next"></param>
        public AppFuncTransition(Func<IDictionary<string, object>, Task> next) : base(null)
        {
            _next = next;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override Task Invoke(IOwinContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            return _next(context.Environment);
        }
    }

}
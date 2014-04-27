namespace FixerUpper
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    static class Map
    {
        public static Func<Func<IDictionary<string, object>, Task>, Func<IDictionary<string, object>, Task>> For(string mapPath, Func<IDictionary<string, object>, Task> mappedFunc)
        {
            if (mapPath == null) throw new ArgumentNullException("mapPath");
            mapPath = '/' + mapPath.Trim('/');

            return next => async env =>
            {
                var path = (string) env["owin.RequestPath"];
                if (path.Equals(mapPath, StringComparison.OrdinalIgnoreCase))
                {
                    var pathBase = env.GetValueOrDefault("owin.RequestPathBase", string.Empty);
                    env["owin.RequestPathBase"] = pathBase + mapPath;
                    await mappedFunc(env);
                }
                else if (path.StartsWith(mapPath + '/'))
                {
                    var pathBase = env.GetValueOrDefault("owin.RequestPathBase", string.Empty);
                    env["owin.RequestPathBase"] = pathBase + mapPath;
                    env["owin.RequestPath"] = path.Substring(mapPath.Length);
                    await mappedFunc(env);
                }
                else
                {
                    await next(env);
                }
            };
        }
    }
}
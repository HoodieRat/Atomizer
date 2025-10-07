using System;
using System.IO;

namespace AtomizeJs.Utils
{
    public static class EnvPaths
    {
        public static string FactsDir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("ATOMIZER_FACTS_DIR");
                if (!string.IsNullOrEmpty(env)) { Directory.CreateDirectory(env); return env; }
                var p = Path.Combine(Directory.GetCurrentDirectory(), "facts");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string PlansDir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("ATOMIZER_PLANS_DIR");
                if (!string.IsNullOrEmpty(env)) { Directory.CreateDirectory(env); return env; }
                var p = Path.Combine(Directory.GetCurrentDirectory(), "plans");
                Directory.CreateDirectory(p);
                return p;
            }
        }
    }
}

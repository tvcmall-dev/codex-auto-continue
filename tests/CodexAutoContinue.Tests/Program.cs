using System;
using System.Linq;
using System.Reflection;

namespace CodexAutoContinue.Tests
{
    public static class Program
    {
        public static int Main()
        {
            var failed = 0; var passed = 0;
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.Name.EndsWith("Tests")))
            {
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (method == null) continue;
                try { method.Invoke(null, null); Console.WriteLine("PASS " + type.Name); passed++; }
                catch (Exception e) { Console.Error.WriteLine("FAIL " + type.Name + ": " + (e.InnerException ?? e)); failed++; }
            }
            Console.WriteLine(passed + " suites passed, " + failed + " failed");
            return failed == 0 && passed > 0 ? 0 : 1;
        }
    }
}

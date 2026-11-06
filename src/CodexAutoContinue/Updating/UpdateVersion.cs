using System.Reflection;

namespace CodexAutoContinue.Updating
{
    public static class UpdateVersion
    {
        public static string Current
        {
            get
            {
                var assembly = typeof(UpdateVersion).Assembly;
                var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return (info == null ? assembly.GetName().Version.ToString(3) : info.InformationalVersion).Split('+')[0];
            }
        }
    }
}

using System.Web.Script.Serialization;

namespace CodexAutoContinue.Infrastructure
{
    public static class Json
    {
        public static string Serialize(object value) { return new JavaScriptSerializer { MaxJsonLength = 4194304 }.Serialize(value); }
        public static T Deserialize<T>(string value) { return new JavaScriptSerializer { MaxJsonLength = 4194304 }.Deserialize<T>(value); }
        public static T Clone<T>(T value) { return Deserialize<T>(Serialize(value)); }
    }
}

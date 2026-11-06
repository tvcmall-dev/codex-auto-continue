using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.Infrastructure
{
    public sealed class SettingsStore
    {
        private readonly string path;
        public SettingsStore(string directory) { Directory.CreateDirectory(directory); path = Path.Combine(directory, "settings.json"); }
        public AppSettings Load(out string warning)
        {
            warning = null;
            if (!File.Exists(path)) return new AppSettings();
            try
            {
                var saved = Json.Deserialize<Envelope>(File.ReadAllText(path, Encoding.UTF8));
                if (saved == null || saved.Settings == null || saved.Version != 1) throw new FormatException();
                var settings = saved.Settings;
                settings.Notifications = settings.Notifications ?? new NotificationSettings();
                settings.Notifications.WebhookUrl = Unprotect(saved.ProtectedUrl);
                settings.Notifications.Secret = Unprotect(saved.ProtectedSecret);
                settings.EffectiveMessage();
                return settings;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException || ex is FormatException || ex is CryptographicException)
            {
                warning = "设置读取失败，已使用默认值；请检查设置文件或重新保存。";
                return new AppSettings();
            }
        }
        public void Save(AppSettings settings)
        {
            settings.EffectiveMessage();
            var copy = Json.Clone(settings);
            var notification = copy.Notifications ?? new NotificationSettings();
            var saved = new Envelope { Version = 1, Settings = copy, ProtectedUrl = Protect(notification.WebhookUrl), ProtectedSecret = Protect(notification.Secret) };
            copy.Notifications = notification;
            notification.WebhookUrl = ""; notification.Secret = "";
            var temp = path + ".tmp";
            File.WriteAllText(temp, Json.Serialize(saved), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        private static string Protect(string value) { return string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)); }
        private static string Unprotect(string value) { return string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        public sealed class Envelope
        {
            public int Version { get; set; }
            public AppSettings Settings { get; set; }
            public string ProtectedUrl { get; set; }
            public string ProtectedSecret { get; set; }
        }
    }
}

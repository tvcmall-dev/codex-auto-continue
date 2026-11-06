using System;
using System.IO;
using System.Linq;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;

namespace CodexAutoContinue.Tests
{
    public static class StorageTests
    {
        public static void Run()
        {
            var folder = Path.Combine(Path.GetTempPath(), "codex-continue-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var settings = new AppSettings { ReplyMode = "custom", CustomMessage = "  继续验证  " };
                settings.Notifications.WebhookUrl = "https://example.invalid/robot?token=private-test-token";
                settings.Notifications.Secret = "private-test-secret";
                var config = new SettingsStore(folder);
                config.Save(settings);
                var raw = File.ReadAllText(Path.Combine(folder, "settings.json"));
                Check(!raw.Contains("private-test"), "Webhook credentials must be DPAPI protected");
                string warning;
                var loaded = config.Load(out warning);
                Check(warning == null && loaded.EffectiveMessage() == "继续验证", "language round trip");
                Check(loaded.Notifications.Secret == settings.Notifications.Secret && loaded.Notifications.WebhookUrl == settings.Notifications.WebhookUrl, "DPAPI round trip");
                var state = new StateStore(folder);
                var session = new SessionRecord { ThreadId = "t1", Title = "中文 ' 引号", Monitoring = true, Attempts = 3, AwaitingConfirmation = true };
                state.SaveSessions(new[] { session });
                var restored = state.LoadSessions().Single();
                Check(restored.Title == session.Title && restored.Attempts == 3 && restored.AwaitingConfirmation, "SQLite session round trip");
                state.AppendEvent(new MonitorEvent { ThreadId = "t1", Kind = "state", Message = "测试事件" });
                Check(state.LoadEvents().Single().Message == "测试事件", "event round trip");
                using (var db = new SqliteDatabase(Path.Combine(folder, "unicode.db")))
                {
                    db.Execute("CREATE TABLE sample(value TEXT)");
                    db.Execute("INSERT INTO sample(value) VALUES(?)", "测试 ' 引号; DROP TABLE sample;");
                    Check((string)db.Query("SELECT value FROM sample")[0]["value"] == "测试 ' 引号; DROP TABLE sample;", "SQLite binding and UTF-8");
                    const string laterFailure = "SELECT 1 AS value UNION ALL SELECT abs(-9223372036854775808)";
                    Check(Convert.ToInt64(db.QueryRows(laterFailure).First()["value"]) == 1, "SQLite row enumeration must stop before evaluating unread rows");
                    try { db.Query(laterFailure); throw new Exception("Materialized queries must still evaluate all rows"); }
                    catch (InvalidOperationException) { }
                    db.QueryRows("SELECT value FROM sample UNION ALL SELECT value FROM sample").First();
                    db.Execute("DROP TABLE sample");
                    Check(db.Query("SELECT name FROM sqlite_master WHERE name='sample'").Count == 0, "Stopping row enumeration must finalize its statement and release table locks");
                }
                File.WriteAllText(Path.Combine(folder, "settings.json"), "invalid-json");
                loaded = config.Load(out warning);
                Check(warning != null && loaded.ReplyMode == "zh", "corrupt config visible safe defaults");
            }
            finally { Directory.Delete(folder, true); }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }
}

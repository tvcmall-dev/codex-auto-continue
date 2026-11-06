using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.Infrastructure
{
    public sealed class StateStore
    {
        private readonly string path;
        public StateStore(string directory)
        {
            Directory.CreateDirectory(directory); path = Path.Combine(directory, "monitor.db");
            using (var db = Open())
            {
                db.Execute("CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,json TEXT NOT NULL)");
                db.Execute("CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT,json TEXT NOT NULL)");
                db.Execute("CREATE TABLE IF NOT EXISTS notifications(event_key TEXT PRIMARY KEY,json TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,due TEXT NOT NULL,done INTEGER NOT NULL DEFAULT 0)");
            }
        }
        private SqliteDatabase Open() { return new SqliteDatabase(path); }
        public List<SessionRecord> LoadSessions()
        {
            using (var db = Open()) return db.Query("SELECT json FROM sessions").Select(x => Json.Deserialize<SessionRecord>((string)x["json"])).ToList();
        }
        public void SaveSessions(IEnumerable<SessionRecord> sessions)
        {
            using (var db = Open())
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    foreach (var session in sessions) db.Execute("INSERT OR REPLACE INTO sessions(id,json) VALUES(?,?)", session.ThreadId, Json.Serialize(session));
                    db.Execute("COMMIT");
                }
                catch { db.Execute("ROLLBACK"); throw; }
            }
        }
        public void AppendEvent(MonitorEvent item)
        {
            using (var db = Open())
            {
                db.Execute("INSERT INTO events(json) VALUES(?)", Json.Serialize(item));
                db.Execute("DELETE FROM events WHERE id NOT IN(SELECT id FROM events ORDER BY id DESC LIMIT 2000)");
            }
        }
        public List<MonitorEvent> LoadEvents()
        {
            using (var db = Open()) return db.Query("SELECT json FROM events ORDER BY id DESC LIMIT 500").Select(x => Json.Deserialize<MonitorEvent>((string)x["json"])).ToList();
        }
        public bool Enqueue(string key, object notice)
        {
            using (var db = Open())
            {
                if (Convert.ToInt64(db.Query("SELECT COUNT(*) AS n FROM notifications WHERE done=0")[0]["n"]) >= 100) return false;
                return db.Execute("INSERT OR IGNORE INTO notifications(event_key,json,due) VALUES(?,?,?)", key, Json.Serialize(notice), DateTime.UtcNow.ToString("o")) > 0;
            }
        }
        public List<Dictionary<string, object>> PendingNotifications()
        {
            using (var db = Open()) return db.Query("SELECT event_key,json,attempts FROM notifications WHERE done=0 AND due<=? ORDER BY due LIMIT 5", DateTime.UtcNow.ToString("o"));
        }
        public void CancelPendingNotifications()
        {
            using (var db = Open()) db.Execute("UPDATE notifications SET done=1 WHERE done=0");
        }
        public void CompleteNotification(string key, int attempts, bool done, DateTime due)
        {
            using (var db = Open())
            {
                db.Execute("UPDATE notifications SET attempts=?,done=?,due=? WHERE event_key=? AND done=0", attempts, done, due.ToString("o"), key);
                db.Execute("DELETE FROM notifications WHERE done=1 AND event_key NOT IN(SELECT event_key FROM notifications WHERE done=1 ORDER BY due DESC LIMIT 2000)");
            }
        }
    }
}

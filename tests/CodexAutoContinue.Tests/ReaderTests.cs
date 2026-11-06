using System;
using System.IO;
using System.Linq;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;
using CodexAutoContinue.Integration;

namespace CodexAutoContinue.Tests
{
    public static class ReaderTests
    {
        public static void Run()
        {
            var directory = Path.Combine(Path.GetTempPath(), "CodexAutoContinue-reader-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var now = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
                var settings = new AppSettings { CodexHome = directory, ReplyMode = "en" };
                using (var state = new SqliteDatabase(Path.Combine(directory, "state_5.sqlite")))
                using (var history = new SqliteDatabase(Path.Combine(directory, "thread_history_1.sqlite")))
                {
                    state.Execute("CREATE TABLE threads(id TEXT PRIMARY KEY,title TEXT,cwd TEXT,updated_at INTEGER,updated_at_ms INTEGER,source TEXT,originator TEXT,archived INTEGER,attached_thread_parent_id TEXT)");
                    history.Execute("CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,rollout_ordinal INTEGER,status TEXT,error_json TEXT,started_at INTEGER,completed_at INTEGER)");
                    history.Execute("CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_id TEXT,rollout_ordinal INTEGER,created_at_ms INTEGER,item_json TEXT,item_type TEXT,updated_at_ordinal INTEGER,completed_at_ms INTEGER)");
                    AddThread(state, "desktop", "Codex Desktop", "vscode", now);
                    AddThread(state, "extension", "VSCode", "vscode", now);
                    AddThread(state, "child", "Codex Desktop", "{\"subagent\":{}}", now);
                    AddThread(state, "archived", "Codex Desktop", "vscode", now);
                    state.Execute("UPDATE threads SET archived=1 WHERE id='archived'");
                    history.Execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?,?)", "desktop", "turn1", 1, "inProgress", null, Seconds(now.AddMinutes(-10)), null);
                    AddItem(history, "turn1", "oldOutput", "agentMessage", "{\"type\":\"agentMessage\",\"text\":\"old output\"}", now.AddMinutes(-5), 2, null);

                    var reader = new LocalCodexReader(() => now);
                    var initial = reader.ReadAsync(settings).GetAwaiter().GetResult();
                    Require(initial.Available, "Fixture databases must be readable.");
                    Require(initial.Observations.Count == 1 && initial.Observations[0].ThreadId == "desktop", "Only unarchived primary Codex Desktop threads are included.");
                    Require(initial.Observations[0].State == SessionState.Unknown && !initial.Observations[0].IsFresh, "Historical inProgress must not imply a live running thread.");

                    now = now.AddSeconds(3);
                    history.Execute("UPDATE thread_turns SET status='failed',error_json=?,completed_at=? WHERE turn_id='turn1'", "{\"message\":\"stream disconnected https://example.invalid/?secret=hidden\",\"codexErrorInfo\":\"other\"}", Seconds(now));
                    var failed = reader.ReadAsync(settings).GetAwaiter().GetResult();
                    var failure = failed.Observations.Single(o => o.Kind == EvidenceKind.TerminalFailure);
                    Require(failure.IsFresh && failure.FailureCode == "network", "A newly projected terminal network failure is eligible evidence.");
                    Require(failed.Observations.Single(o => o.Kind == EvidenceKind.Snapshot).IsTerminalFailure && failed.Observations.Single(o => o.Kind == EvidenceKind.Snapshot).CompletedAtUtc.HasValue,
                        "Snapshot must expose final failure identity and actual completion time for pre-send checks and pending reconciliation.");
                    Require(!failure.Reason.Contains("secret") && !failure.Reason.Contains("example.invalid"), "Failure reasons must not disclose raw error text or tokens.");
                    Require(!reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.Kind == EvidenceKind.TerminalFailure), "A final failure must not be replayed each poll.");

                    now = now.AddSeconds(3).AddMilliseconds(600);
                    history.Execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?,?)", "desktop", "turn2", 5, "inProgress", null, Seconds(now), null);
                    AddItem(history, "turn2", "user1", "userMessage", "{\"type\":\"userMessage\",\"content\":[{\"type\":\"text\",\"text\":\"continue\"}]}", now, 6, null);
                    var started = reader.ReadAsync(settings).GetAwaiter().GetResult();
                    Require(started.Observations.Any(o => o.Kind == EvidenceKind.Started), "New turn metadata emits a start event.");
                    Require(!started.Observations.Any(o => o.Kind == EvidenceKind.Progress), "A turn start or user message is not recovery evidence.");
                    Require(started.Observations.Single(o => o.Kind == EvidenceKind.UserMessage).IsContinuationMessage, "An echoed continuation must preserve its incident budget.");
                    Require(started.Observations.Single(o => o.Kind == EvidenceKind.UserMessage).UserMessageFingerprint == MessageIdentity.Fingerprint("continue"), "User content is represented by its fingerprint without exposing transcript text.");
                    var withoutOutput = new SessionRecord { ThreadId = "desktop" };
                    var waiting = new SessionRecord
                    {
                        ThreadId = "desktop", TurnId = "turn1", FailedTurnId = "turn1", IncidentId = "waiting",
                        AwaitingConfirmation = true, DispatchUtc = now.AddSeconds(-1), State = SessionState.Pending, Attempts = 1
                    };
                    foreach (var observation in started.Observations)
                    {
                        new RecoveryEngine().Apply(withoutOutput, observation, settings, now);
                        new RecoveryEngine().Apply(waiting, observation, settings, now);
                    }
                    Require(withoutOutput.State == SessionState.Running, "A new turn with a millisecond user message must appear running before its first output.");
                    Require(waiting.State == SessionState.Pending && waiting.AwaitingConfirmation && waiting.Attempts == 1 && !waiting.Events.Any(e => e.Kind == "recovery"),
                        "Turn start alone must preserve pending recovery confirmation and its attempt budget.");

                    now = now.AddSeconds(3);
                    AddItem(history, "turn2", "emptyOutput", "agentMessage", "{\"type\":\"agentMessage\",\"text\":\"\"}", now, 7, null);
                    Require(!reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.Kind == EvidenceKind.Progress), "An empty assistant item is not execution evidence.");
                    history.Execute("UPDATE thread_items SET item_json=?,updated_at_ordinal=8 WHERE item_id='emptyOutput'", "{\"type\":\"agentMessage\",\"text\":\"new actual model output\"}");
                    var progressed = reader.ReadAsync(settings).GetAwaiter().GetResult();
                    Require(progressed.Observations.Any(o => o.Kind == EvidenceKind.Progress && o.IsFresh && o.TurnId == "turn2"), "Actual new assistant output confirms execution in the new turn.");
                    Require(!reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.Kind == EvidenceKind.Progress), "The same output is emitted once.");

                    now = now.AddSeconds(3);
                    AddItem(history, "turn2", "tool1", "commandExecution", "{\"type\":\"commandExecution\",\"status\":\"inProgress\"}", now, 9, null);
                    Require(!reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.Kind == EvidenceKind.Progress), "A newly created tool request is not proof of execution.");
                    history.Execute("UPDATE thread_items SET completed_at_ms=?,updated_at_ordinal=10 WHERE item_id='tool1'", Milliseconds(now));
                    Require(reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.Kind == EvidenceKind.Progress), "A newly completed tool provides execution evidence.");

                    now = now.AddSeconds(3);
                    history.Execute("UPDATE thread_turns SET status='failed',error_json=?,completed_at=? WHERE turn_id='turn2'", "{\"message\":\"limited\",\"codexErrorInfo\":{\"responseTooManyFailedAttempts\":{\"httpStatusCode\":429}}}", Seconds(now));
                    var rateLimit = reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Single(o => o.Kind == EvidenceKind.TerminalFailure);
                    Require(rateLimit.FailureCode == "rateLimit" && rateLimit.RetryAtUtc == null, "HTTP 429 must not invent subscription quota or reset timing.");

                    now = now.AddSeconds(3);
                    history.Execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?,?)", "desktop", "oldImported", 20, "failed", "{\"codexErrorInfo\":\"serverOverloaded\"}", Seconds(now.AddDays(-1)), Seconds(now.AddDays(-1)));
                    Require(!reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.Kind == EvidenceKind.TerminalFailure), "Imported historical failures are not fresh merely because observed later.");

                    var restart = new LocalCodexReader(() => now).ReadAsync(settings).GetAwaiter().GetResult();
                    Require(!restart.Observations.Any(o => o.IsFresh), "Restart establishes a baseline and never replays failures.");

                    history.Execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?,?)", "desktop", "oldInProgress", 30, "inProgress", null, Seconds(now.AddDays(-1)), null);
                    AddItem(history, "oldInProgress", "importedOutput", "agentMessage", "{\"type\":\"agentMessage\",\"text\":\"historical output\"}", now.AddDays(-1), 31, null);
                    Require(!reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.IsFresh), "A newly imported stale in-progress turn is not live evidence.");
                    history.Execute("UPDATE thread_items SET updated_at_ordinal=32 WHERE item_id='importedOutput'");
                    Require(!reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.Kind == EvidenceKind.Progress), "Repairing a historical projection must not imply fresh execution.");

                    now = now.AddSeconds(3).AddMilliseconds(600);
                    history.Execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?,?)", "desktop", "sameSecond", 40, "failed", "{\"codexErrorInfo\":\"serverOverloaded\"}", Seconds(now), Seconds(now));
                    AddItem(history, "sameSecond", "sameSecondUser", "userMessage", "{\"type\":\"userMessage\",\"content\":[{\"type\":\"text\",\"text\":\"a new task\"}]}", now, 41, null);
                    var sameSecond = reader.ReadAsync(settings).GetAwaiter().GetResult();
                    Require(sameSecond.Observations.Single(o => o.Kind == EvidenceKind.TerminalFailure).TimestampUtc >= sameSecond.Observations.Single(o => o.Kind == EvidenceKind.UserMessage).TimestampUtc,
                        "Second-precision completion must not sort before a same-turn millisecond user message.");

                    history.Execute("UPDATE thread_turns SET status='inProgress',completed_at=NULL WHERE turn_id='sameSecond'");
                    var sameSecondStartupReader = new LocalCodexReader(() => now);
                    sameSecondStartupReader.ReadAsync(settings).GetAwaiter().GetResult();
                    now = now.AddMilliseconds(100);
                    history.Execute("UPDATE thread_turns SET status='failed',completed_at=? WHERE turn_id='sameSecond'", Seconds(now));
                    Require(sameSecondStartupReader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Any(o => o.Kind == EvidenceKind.TerminalFailure && o.IsFresh),
                        "A final failure observed after baseline in the same second as application startup must remain fresh.");

                    var coalescedStart = now.AddSeconds(1);
                    now = now.AddSeconds(2);
                    history.Execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?,?)", "desktop", "coalescedTurn", 50, "inProgress", null, Seconds(coalescedStart), null);
                    AddItem(history, "coalescedTurn", "coalescedUser", "userMessage", "{\"type\":\"userMessage\",\"content\":[{\"type\":\"text\",\"text\":\"continue\"}]}", coalescedStart, 51, null);
                    AddItem(history, "coalescedTurn", "coalescedOutput", "agentMessage", "{\"type\":\"agentMessage\",\"text\":\"actual new output\"}", coalescedStart.AddMilliseconds(100), 52, null);
                    var coalesced = reader.ReadAsync(settings).GetAwaiter().GetResult();
                    var pending = new SessionRecord
                    {
                        ThreadId = "desktop", TurnId = "sameSecond", FailedTurnId = "sameSecond", IncidentId = "incident",
                        AwaitingConfirmation = true, DispatchUtc = coalescedStart.AddSeconds(-1), State = SessionState.Pending, Attempts = 1
                    };
                    var engine = new RecoveryEngine();
                    foreach (var observation in coalesced.Observations) engine.Apply(pending, observation, settings, now);
                    Require(!pending.AwaitingConfirmation && pending.State == SessionState.Running,
                        "Turn start detection must not suppress real output coalesced into the same reader poll.");
                    RecentProgressSnapshots(history, settings, now);
                    MaskedRecentProgressSnapshots(history, settings, now);
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void RecentProgressSnapshots(SqliteDatabase history, AppSettings settings, DateTime now)
        {
            var cases = new[]
            {
                new { Name = "recent-output", Type = "agentMessage", Text = "visible output", Created = now.AddSeconds(-30), Completed = (DateTime?)null, Status = "inProgress", Expected = SessionState.Running },
                new { Name = "recent-tool", Type = "commandExecution", Text = "", Created = now.AddMinutes(-5), Completed = (DateTime?)now.AddSeconds(-20), Status = "inProgress", Expected = SessionState.Running },
                new { Name = "stale-output", Type = "agentMessage", Text = "old output", Created = now.AddSeconds(-121), Completed = (DateTime?)null, Status = "inProgress", Expected = SessionState.Unknown },
                new { Name = "near-future-output", Type = "agentMessage", Text = "future output", Created = now.AddSeconds(20), Completed = (DateTime?)null, Status = "inProgress", Expected = SessionState.Unknown },
                new { Name = "future-output", Type = "agentMessage", Text = "future output", Created = now.AddSeconds(61), Completed = (DateTime?)null, Status = "inProgress", Expected = SessionState.Unknown },
                new { Name = "empty-output", Type = "agentMessage", Text = "", Created = now.AddSeconds(-20), Completed = (DateTime?)null, Status = "inProgress", Expected = SessionState.Unknown },
                new { Name = "unfinished-tool", Type = "commandExecution", Text = "", Created = now.AddSeconds(-20), Completed = (DateTime?)null, Status = "inProgress", Expected = SessionState.Unknown },
                new { Name = "finished-turn", Type = "agentMessage", Text = "final output", Created = now.AddSeconds(-20), Completed = (DateTime?)null, Status = "completed", Expected = SessionState.Completed },
                new { Name = "stopped-turn", Type = "agentMessage", Text = "prior output", Created = now.AddSeconds(-20), Completed = (DateTime?)null, Status = "interrupted", Expected = SessionState.Stopped }
            };
            var ordinal = 100L;
            foreach (var item in cases)
            {
                history.Execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?,?)", "desktop", item.Name, ordinal++, item.Status, null, Seconds(now.AddMinutes(-10)), item.Status == "inProgress" ? (object)null : Seconds(now));
                AddItem(history, item.Name, item.Name + "-item", item.Type, Json.Serialize(new { type = item.Type, text = item.Text }), item.Created, ordinal++, item.Completed.HasValue ? (long?)Milliseconds(item.Completed.Value) : null);
                var reader = new LocalCodexReader(() => now);
                var baseline = reader.ReadAsync(settings).GetAwaiter().GetResult();
                var snapshot = baseline.Observations.Single(o => o.Kind == EvidenceKind.Snapshot);
                Require(snapshot.State == item.Expected, item.Name + " baseline must display " + item.Expected + " based on recent actual activity.");
                Require(baseline.Observations.All(o => !o.IsFresh), item.Name + " baseline must never emit automatic recovery or failure evidence.");
                if (item.Expected != SessionState.Running) continue;
                var active = new SessionRecord { ThreadId = "desktop" };
                var pending = new SessionRecord { ThreadId = "desktop", State = SessionState.Pending, AwaitingConfirmation = true, Attempts = 2, FailedTurnId = "previous-failed", IncidentId = "pending", DispatchUtc = now.AddMinutes(-1) };
                var engine = new RecoveryEngine();
                engine.Apply(active, snapshot, settings, now);
                engine.Apply(pending, snapshot, settings, now);
                Require(active.State == SessionState.Running && active.LastEvidenceUtc == default(DateTime) && !active.FreshFailure && !active.NextRetryUtc.HasValue,
                    "A recent baseline may display running without enabling automatic continuation.");
                Require(pending.State == SessionState.Pending && pending.AwaitingConfirmation && pending.Attempts == 2 && !pending.Events.Any(e => e.Kind == "recovery"),
                    "A recent running snapshot cannot confirm an uncertain delivery.");
                now = now.AddMinutes(3);
                var expired = reader.ReadAsync(settings).GetAwaiter().GetResult().Observations.Single(o => o.Kind == EvidenceKind.Snapshot);
                Require(expired.State == SessionState.Unknown && expired.Id != snapshot.Id, "Activity expiration must change the snapshot identity even when thread metadata is unchanged.");
                engine.Apply(active, expired, settings, now);
                Require(active.State == SessionState.Unknown, "A baseline running display must expire without requiring new metadata or authoritative evidence.");
                now = now.AddMinutes(-3);
            }
        }

        private static void MaskedRecentProgressSnapshots(SqliteDatabase history, AppSettings settings, DateTime now)
        {
            var ordinal = 1000L;
            foreach (var type in new[] { "agentMessage", "commandExecution" })
            foreach (var maskCount in new[] { 1, 128 })
            {
                var turn = "masked-" + type + "-" + maskCount;
                history.Execute("BEGIN");
                history.Execute("INSERT INTO thread_turns VALUES(?,?,?,?,?,?,?)", "desktop", turn, ordinal++, "inProgress", null, Seconds(now.AddMinutes(-10)), null);
                AddItem(history, turn, turn + "-actual", type, Json.Serialize(new { type, text = "actual execution" }),
                    type == "agentMessage" ? now.AddSeconds(-30) : now.AddMinutes(-5), ordinal++, type == "agentMessage" ? (long?)null : Milliseconds(now.AddSeconds(-20)));
                for (var index = 0; index < maskCount; index++)
                    AddItem(history, turn, turn + "-empty-" + index, "agentMessage", "{\"text\":\"\"}", now.AddSeconds(-1), ordinal++, null);
                history.Execute("COMMIT");
                var reader = new LocalCodexReader(() => now);
                var baseline = reader.ReadAsync(settings).GetAwaiter().GetResult();
                Require(baseline.Observations.Single(o => o.Kind == EvidenceKind.Snapshot).State == SessionState.Running,
                    "Recent " + type + " must remain visible behind " + maskCount + " empty assistant placeholders.");
                Require(baseline.Observations.All(o => !o.IsFresh), "Looking behind placeholders must not turn baseline activity into new recovery evidence.");
                var pending = new SessionRecord { ThreadId = "desktop", State = SessionState.Pending, AwaitingConfirmation = true, Attempts = 2, FailedTurnId = "previous-failed", IncidentId = "pending", DispatchUtc = now.AddMinutes(-1) };
                var engine = new RecoveryEngine();
                foreach (var observation in baseline.Observations) engine.Apply(pending, observation, settings, now);
                Require(pending.State == SessionState.Pending && pending.AwaitingConfirmation && !pending.Events.Any(e => e.Kind == "recovery"),
                    "A running snapshot found behind placeholders must not confirm an uncertain delivery.");
                now = now.AddSeconds(1);
                AddItem(history, turn, turn + "-new-actual", "agentMessage", "{\"text\":\"new output\"}", now, ordinal++, null);
                AddItem(history, turn, turn + "-new-empty", "agentMessage", "{\"text\":\"\"}", now, ordinal++, null);
                var following = reader.ReadAsync(settings).GetAwaiter().GetResult();
                Require(following.Observations.Single(o => o.Kind == EvidenceKind.Snapshot).State == SessionState.Running &&
                    !following.Observations.Any(o => o.Kind == EvidenceKind.Progress),
                    "Snapshot fallback must preserve the existing latest-candidate-only fresh progress policy.");
                foreach (var observation in following.Observations) engine.Apply(pending, observation, settings, now);
                Require(pending.AwaitingConfirmation && pending.Attempts == 2, "Display fallback must preserve pending recovery and its budget on later polls.");
                now = now.AddSeconds(-1);
            }
        }

        private static void AddThread(SqliteDatabase state, string id, string originator, string source, DateTime time)
        {
            state.Execute("INSERT INTO threads VALUES(?,?,?,?,?,?,?,?,?)", id, "测试会话", "C:\\Example", Seconds(time), Milliseconds(time), source, originator, 0, null);
        }
        private static void AddItem(SqliteDatabase history, string turn, string id, string type, string json, DateTime time, long ordinal, long? completed)
        {
            history.Execute("INSERT INTO thread_items VALUES(?,?,?,?,?,?,?,?,?)", "desktop", turn, id, ordinal, Milliseconds(time), json, type, ordinal, completed);
        }
        private static long Seconds(DateTime time) { return new DateTimeOffset(time).ToUnixTimeSeconds(); }
        private static long Milliseconds(DateTime time) { return new DateTimeOffset(time).ToUnixTimeMilliseconds(); }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}

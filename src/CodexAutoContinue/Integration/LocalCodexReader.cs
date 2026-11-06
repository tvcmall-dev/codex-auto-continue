using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;

namespace CodexAutoContinue.Integration
{
    public sealed class LocalCodexReader : ISessionReader
    {
        private readonly object sync = new object();
        private readonly Func<DateTime> utcNow;
        private readonly Dictionary<string, string> turnVersions = new Dictionary<string, string>();
        private readonly Dictionary<string, string> progressVersions = new Dictionary<string, string>();
        private readonly Dictionary<string, string> userVersions = new Dictionary<string, string>();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private DateTime startedUtc;
        private bool baselined;
        private string currentHome;

        public LocalCodexReader() : this(() => DateTime.UtcNow) { }
        public LocalCodexReader(Func<DateTime> utcNow)
        {
            this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            startedUtc = utcNow();
        }

        public Task<ReadResult> ReadAsync(AppSettings settings)
        {
            return Task.Run(() => { lock (sync) return Read(settings); });
        }

        private ReadResult Read(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            try
            {
                var home = Path.GetFullPath(settings.CodexHome ?? "");
                if (!String.Equals(currentHome, home, StringComparison.OrdinalIgnoreCase))
                {
                    currentHome = home;
                    startedUtc = utcNow();
                    baselined = false;
                    turnVersions.Clear(); progressVersions.Clear(); userVersions.Clear();
                }
                if (!Directory.Exists(home)) return Unavailable("未找到 Codex 数据目录，请在设置中检查路径。");
                var statePath = FindDatabase(home, "state_");
                var historyPath = FindDatabase(home, "thread_history_");
                if (statePath == null || historyPath == null)
                    return Unavailable("尚未找到受支持的 Codex 会话数据库，请启动 Codex 桌面端后重试。");

                var result = new ReadResult { Available = true, Status = "本地桌面事件投影 · 只读监测；未验证桌面控制端点" };
                using (var state = new SqliteDatabase(statePath, true))
                using (var history = new SqliteDatabase(historyPath, true))
                {
                    var threads = state.Query(@"SELECT id,title,cwd,COALESCE(updated_at_ms,updated_at*1000) AS updated_ms
FROM threads WHERE archived=0 AND originator='Codex Desktop' AND source='vscode'
AND attached_thread_parent_id IS NULL ORDER BY COALESCE(updated_at_ms,updated_at*1000) DESC LIMIT 120");
                    foreach (var thread in threads) ReadThread(history, settings, thread, result);
                }
                baselined = true;
                return result;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is ArgumentException || error is System.Runtime.InteropServices.ExternalException)
            {
                return Unavailable("无法读取当前 Codex 数据库，可能正忙或版本不兼容；请稍后刷新。不会据此自动续接。");
            }
        }

        private void ReadThread(SqliteDatabase history, AppSettings settings, Dictionary<string, object> thread, ReadResult result)
        {
            var threadId = Text(thread, "id");
            var rows = history.Query(@"SELECT turn_id,status,error_json,started_at,completed_at,rollout_ordinal
FROM thread_turns WHERE thread_id=? ORDER BY rollout_ordinal DESC LIMIT 1", threadId);
            var turn = rows.FirstOrDefault();
            var turnId = turn == null ? "" : Text(turn, "turn_id");
            var status = turn == null ? "" : Text(turn, "status");
            var activity = FromMilliseconds(Number(thread, "updated_ms"));
            var snapshotState = status == "completed" ? SessionState.Completed : status == "interrupted" ? SessionState.Stopped : SessionState.Unknown;
            var snapshot = Observation(thread, turnId, EvidenceKind.Snapshot, snapshotState, activity,
                "snapshot:" + threadId + ":" + turnId + ":" + status + ":" + Number(thread, "updated_ms"));
            snapshot.IsTerminalFailure = status == "failed";
            if (turn != null && Number(turn, "completed_at") > 0 && (status == "failed" || status == "completed" || status == "interrupted"))
                snapshot.CompletedAtUtc = FromSeconds(Number(turn, "completed_at"));
            snapshot.Reason = status == "failed" ? "历史记录包含失败；仅对本次监测后新确认的异常安排续接。"
                : status == "inProgress" ? "数据库记录为进行中；尚无本次监测后的执行证据，可能正在等待或已离线。"
                : status == "completed" ? "最近记录的执行轮次已完成。"
                : status == "interrupted" ? "最近执行轮次已被用户停止。" : "等待新的桌面会话事件。";
            result.Observations.Add(snapshot);
            if (turn == null) return;

            var version = turnId + ":" + status + ":" + Number(turn, "completed_at");
            string previousTurn;
            var turnChanged = !turnVersions.TryGetValue(threadId, out previousTurn) || previousTurn != version;
            turnVersions[threadId] = version;
            ReadUserMessage(history, settings, thread, turnId, result);
            var turnTime = FromSeconds(Number(turn, status == "inProgress" ? "started_at" : "completed_at"));
            var observationTime = utcNow();
            var startupSecond = new DateTime(startedUtc.Ticks - startedUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            var freshTurn = baselined && turnChanged && turnTime >= startupSecond && turnTime <= observationTime.AddMinutes(1);
            if (freshTurn)
            {
                if (status == "failed")
                {
                    // Turn timestamps have second precision; observation time keeps final states after their millisecond item events.
                    var failure = Observation(thread, turnId, EvidenceKind.TerminalFailure, SessionState.Failed, observationTime, "failed:" + threadId + ":" + turnId);
                    failure.IsFresh = true;
                    ClassifyFailure(Text(turn, "error_json"), failure);
                    result.Observations.Add(failure);
                }
                else if (status == "completed" || status == "interrupted" || status == "inProgress")
                {
                    var kind = status == "completed" ? EvidenceKind.Completed : status == "interrupted" ? EvidenceKind.Stopped : EvidenceKind.Started;
                    var state = status == "completed" ? SessionState.Completed : status == "interrupted" ? SessionState.Stopped : SessionState.Unknown;
                    var observation = Observation(thread, turnId, kind, state, kind == EvidenceKind.Started ? turnTime : observationTime, kind + ":" + threadId + ":" + turnId);
                    observation.IsFresh = true;
                    observation.Reason = status == "completed" ? "观察到新的执行轮次成功完成。" : status == "interrupted" ? "观察到用户停止执行。" : "观察到新的执行轮次；等待实际输出或工具完成以确认执行。";
                    result.Observations.Add(observation);
                }
            }
            ReadProgress(history, thread, turnId, status, result);
        }

        private void ReadUserMessage(SqliteDatabase history, AppSettings settings, Dictionary<string, object> thread, string turnId, ReadResult result)
        {
            var item = history.Query(@"SELECT item_id,item_json,created_at_ms FROM thread_items
WHERE thread_id=? AND turn_id=? AND item_type='userMessage' ORDER BY rollout_ordinal DESC LIMIT 1", Text(thread, "id"), turnId).FirstOrDefault();
            if (item == null) return;
            var id = Text(item, "item_id");
            var threadId = Text(thread, "id");
            string previous;
            var changed = !userVersions.TryGetValue(threadId, out previous) || previous != id;
            userVersions[threadId] = id;
            var timestamp = FromMilliseconds(Number(item, "created_at_ms"));
            if (!baselined || !changed || timestamp < startedUtc || timestamp > utcNow().AddMinutes(1)) return;
            var observation = Observation(thread, turnId, EvidenceKind.UserMessage, SessionState.Unknown, timestamp, "user:" + threadId + ":" + id);
            observation.IsFresh = true;
            observation.Reason = "观察到新的用户消息。";
            var document = Parse(Text(item, "item_json"));
            object content;
            if (document != null && document.TryGetValue("content", out content) && content is IEnumerable)
            {
                var text = String.Concat(((IEnumerable)content).Cast<object>().OfType<Dictionary<string, object>>()
                    .Where(part => Text(part, "type") == "text").Select(part => Text(part, "text"))).Trim();
                observation.UserMessageFingerprint = MessageIdentity.Fingerprint(text);
                observation.IsContinuationMessage = String.Equals(text, settings.EffectiveMessage(), StringComparison.Ordinal);
            }
            result.Observations.Add(observation);
        }

        private void ReadProgress(SqliteDatabase history, Dictionary<string, object> thread, string turnId, string turnStatus, ReadResult result)
        {
            var item = history.Query(@"SELECT item_id,item_type,item_json,created_at_ms,completed_at_ms,updated_at_ordinal
FROM thread_items WHERE thread_id=? AND turn_id=? AND
(item_type='agentMessage' OR (item_type IN ('commandExecution','mcpToolCall','dynamicToolCall','fileChange') AND completed_at_ms IS NOT NULL))
ORDER BY rollout_ordinal DESC LIMIT 1", Text(thread, "id"), turnId).FirstOrDefault();
            if (item == null) return;
            var threadId = Text(thread, "id");
            var signature = turnId + ":" + Text(item, "item_id") + ":" + Number(item, "updated_at_ordinal") + ":" + Number(item, "completed_at_ms");
            string previous;
            var changed = !progressVersions.TryGetValue(threadId, out previous) || previous != signature;
            progressVersions[threadId] = signature;
            if (!baselined || !changed || turnStatus != "inProgress") return;
            var timestamp = FromMilliseconds(Math.Max(Number(item, "created_at_ms"), Number(item, "completed_at_ms")));
            // Historical projection repair can change ordinals without live execution.
            if (timestamp < startedUtc || timestamp > utcNow().AddMinutes(1)) return;
            if (Text(item, "item_type") == "agentMessage")
            {
                var document = Parse(Text(item, "item_json"));
                if (document == null || String.IsNullOrWhiteSpace(Text(document, "text"))) return;
            }
            var observation = Observation(thread, turnId, EvidenceKind.Progress, SessionState.Running,
                timestamp, "progress:" + threadId + ":" + signature);
            observation.IsFresh = true;
            observation.Reason = "观察到新的模型输出或已完成的工具执行。";
            result.Observations.Add(observation);
        }

        private void ClassifyFailure(string value, SessionObservation observation)
        {
            var document = Parse(value);
            var code = "other";
            var http = 0L;
            var message = "";
            if (document != null)
            {
                message = Text(document, "message").ToLowerInvariant();
                object info;
                if (document.TryGetValue("codexErrorInfo", out info))
                {
                    if (info is string) code = (string)info;
                    else if (info is Dictionary<string, object>)
                    {
                        var tagged = (Dictionary<string, object>)info;
                        var entry = tagged.FirstOrDefault(); code = entry.Key ?? "other";
                        if (entry.Value is Dictionary<string, object>) http = Number((Dictionary<string, object>)entry.Value, "httpStatusCode");
                    }
                }
            }
            if (http == 429 || code == "rateLimitExceeded")
            {
                observation.FailureCode = "rateLimit";
                observation.Reason = "服务返回速率限制；未提供可靠重试时间，需要人工确认。";
            }
            else if (code == "usageLimitExceeded")
            {
                observation.FailureCode = "usageLimit";
                observation.Reason = "Codex 额度不足；未提供可验证的重置时间，等待人工确认。";
            }
            else if (http >= 400 && http < 500 || code == "unauthorized" || code == "badRequest" || message.Contains("unauthorized") || message.Contains("insufficient credit"))
            {
                observation.FailureCode = "nonRecoverable";
                observation.Reason = "鉴权、请求或余额异常，需要人工处理。";
            }
            else if (code == "serverOverloaded" || code == "internalServerError" ||
                code == "httpConnectionFailed" || code == "responseStreamConnectionFailed" || code == "responseStreamDisconnected" ||
                code == "responseTooManyFailedAttempts" && (http == 0 || http >= 500) ||
                code == "other" && (message.Contains("stream disconnected") || message.Contains("network error") || message.Contains("connection reset") || message.Contains("connection refused") || message.Contains("timed out") || message.Contains("timeout")))
            {
                observation.FailureCode = "network";
                observation.Reason = "执行轮次因网络连接或临时服务异常而失败。";
            }
            else
            {
                observation.FailureCode = "nonRecoverable";
                observation.Reason = "执行失败，错误类型不适合自动续接，需要人工检查。";
            }
        }

        private Dictionary<string, object> Parse(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            try { return json.DeserializeObject(value) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        private static SessionObservation Observation(Dictionary<string, object> thread, string turnId, EvidenceKind kind, SessionState state, DateTime time, string id)
        {
            return new SessionObservation
            {
                Id = id, ThreadId = Text(thread, "id"), Title = DisplayTitle(Text(thread, "title")),
                Workspace = Text(thread, "cwd"), TurnId = turnId, Kind = kind, State = state, TimestampUtc = time
            };
        }
        private static string DisplayTitle(string title)
        {
            if (String.IsNullOrWhiteSpace(title)) return "未命名会话";
            var firstLine = title.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "未命名会话";
            firstLine = System.Text.RegularExpressions.Regex.Replace(firstLine, @"https?://\S+", "[链接]", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return firstLine.Length > 120 ? firstLine.Substring(0, 120) + "…" : firstLine;
        }
        private static string FindDatabase(string home, string prefix)
        {
            return Directory.EnumerateFiles(home, prefix + "*.sqlite", SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Version = DatabaseVersion(path, prefix) }).Where(item => item.Version >= 0)
                .OrderByDescending(item => item.Version).Select(item => item.Path).FirstOrDefault();
        }
        private static int DatabaseVersion(string path, string prefix)
        {
            int result;
            return Int32.TryParse(Path.GetFileNameWithoutExtension(path).Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out result) ? result : -1;
        }
        private static string Text(Dictionary<string, object> row, string name)
        {
            object value;
            return row.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
        private static long Number(Dictionary<string, object> row, string name)
        {
            long result;
            return Int64.TryParse(Text(row, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }
        private static DateTime FromSeconds(long value) { return FromMilliseconds(value > 0 && value < 253402300800L ? value * 1000 : 0); }
        private static DateTime FromMilliseconds(long value) { return value > 0 && value < 253402300800000L ? DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc); }
        private static ReadResult Unavailable(string reason) { return new ReadResult { Available = false, Status = reason }; }
    }
}

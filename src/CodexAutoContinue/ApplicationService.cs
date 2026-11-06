using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;
using CodexAutoContinue.Integration;
using CodexAutoContinue.Notifications;
using Microsoft.Win32;

namespace CodexAutoContinue
{
    public sealed class ApplicationService : IApplicationService
    {
        private readonly object sync = new object();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly RecoveryEngine engine = new RecoveryEngine();
        private readonly ISessionReader reader;
        private readonly ICliSender sender;
        private readonly StateStore store;
        private readonly SettingsStore settingsStore;
        private readonly WebhookClient webhooks;
        private readonly Func<DateTime> now;
        private readonly Dictionary<string, SessionRecord> sessions;
        private readonly List<MonitorEvent> events;
        private AppSettings settings;
        private Task notifications = Task.CompletedTask;
        private bool disposed;
        public event Action Changed;
        public string DataDirectory { get; private set; }
        public string ConnectionStatus { get; private set; } = "等待连接本地 Codex 数据。";
        public bool IsConnected { get; private set; }
        public string NotificationStatus { get; private set; } = "通知未配置";
        public AppSettings Settings { get { lock (sync) return Json.Clone(settings); } }
        public IReadOnlyList<SessionRecord> Sessions { get { lock (sync) return sessions.Values.OrderByDescending(x => x.LastActivityUtc).Select(Json.Clone).ToList(); } }
        public IReadOnlyList<MonitorEvent> Events { get { lock (sync) return events.Select(Json.Clone).ToList(); } }
        public ApplicationService(string directory = null, ISessionReader reader = null, ICliSender sender = null, WebhookClient webhooks = null, Func<DateTime> utcNow = null)
        {
            DataDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAutoContinue");
            this.reader = reader ?? new LocalCodexReader(); this.sender = sender ?? new CliSender();
            this.webhooks = webhooks ?? new WebhookClient(); now = utcNow ?? (() => DateTime.UtcNow);
            settingsStore = new SettingsStore(DataDirectory); string warning;
            settings = settingsStore.Load(out warning);
            store = new StateStore(DataDirectory);
            sessions = store.LoadSessions().ToDictionary(x => x.ThreadId);
            events = store.LoadEvents();
            foreach (var session in sessions.Values)
            {
                session.FreshFailure = false; session.NextRetryUtc = null;
                session.LastEvidenceUtc = default(DateTime);
                if (!session.AwaitingConfirmation && session.State != SessionState.Completed && session.State != SessionState.Stopped)
                { session.State = SessionState.Unknown; session.Reason = "程序已重启，等待新的运行事件；历史异常不会自动续接。"; }
            }
            if (warning != null) AddEvent(new MonitorEvent { Kind = "settings", Message = warning });
            NotificationStatus = settings.Notifications.Enabled ? "通知已启用" : "通知未启用";
        }
        public async Task RefreshAsync()
        {
            if (disposed || !await gate.WaitAsync(0)) return;
            try
            {
                var result = await reader.ReadAsync(Settings);
                ApplyResult(result);
                if (result.Available)
                {
                    List<string> due;
                    lock (sync) due = sessions.Values.Where(x => engine.CanDispatch(x, settings, now())).Select(x => x.ThreadId).ToList();
                    foreach (var id in due)
                    {
                        // Re-read immediately before each send; newer turns cancel stale scheduling.
                        var check = await reader.ReadAsync(Settings);
                        ApplyResult(check);
                        bool eligible;
                        lock (sync) eligible = TargetStillFailed(check, id) && engine.CanDispatch(sessions[id], settings, now());
                        if (eligible) await DispatchAsync(id);
                    }
                }
                lock (sync) store.SaveSessions(sessions.Values);
                if (!disposed && notifications.IsCompleted) notifications = ProcessNotificationsAsync();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
            {
                IsConnected = false; ConnectionStatus = "刷新失败，请检查本地数据和路径；已暂停自动发送。";
                lock (sync) foreach (var session in sessions.Values) { session.FreshFailure = false; session.NextRetryUtc = null; }
            }
            finally { gate.Release(); Changed?.Invoke(); }
        }
        private void ApplyResult(ReadResult result)
        {
            lock (sync)
            {
                IsConnected = result.Available; ConnectionStatus = result.Status;
                if (!result.Available)
                {
                    foreach (var session in sessions.Values)
                    {
                        session.FreshFailure = false; session.NextRetryUtc = null;
                        if (!session.AwaitingConfirmation && session.State != SessionState.Completed && session.State != SessionState.Stopped)
                        { session.State = SessionState.Unknown; session.Reason = "数据连接不可用，等待重新确认。"; }
                    }
                    return;
                }
                var present = new HashSet<string>(result.Observations.Select(x => x.ThreadId));
                foreach (var missing in sessions.Values.Where(x => !present.Contains(x.ThreadId)))
                {
                    missing.FreshFailure = false; missing.NextRetryUtc = null;
                    if (!missing.AwaitingConfirmation) { missing.State = SessionState.Unknown; missing.Reason = "当前来源未返回该会话，已暂停续接。"; }
                }
                foreach (var observation in result.Observations)
                {
                    SessionRecord session;
                    if (!sessions.TryGetValue(observation.ThreadId, out session))
                    { session = new SessionRecord { ThreadId = observation.ThreadId }; sessions.Add(observation.ThreadId, session); }
                    if (session.AwaitingConfirmation && observation.Kind == EvidenceKind.UserMessage && !string.IsNullOrEmpty(observation.UserMessageFingerprint))
                        observation.IsContinuationMessage = observation.UserMessageFingerprint == MessageIdentity.Fingerprint(session.DispatchMessage);
                    var previous = new HashSet<MonitorEvent>(session.Events);
                    engine.Apply(session, observation, settings, now());
                    foreach (var item in session.Events.Where(x => !previous.Contains(x)).Reverse()) RecordSessionEvent(session, item);
                }
                foreach (var session in sessions.Values.Where(x => x.State == SessionState.Running && !x.AwaitingConfirmation && x.LastEvidenceUtc != default(DateTime) && now() - x.LastEvidenceUtc > TimeSpan.FromMinutes(2)))
                {
                    session.State = SessionState.Unknown;
                    session.Reason = "超过 2 分钟没有新的执行证据，运行状态待确认；不会据此自动续接。";
                }
            }
        }
        private async Task DispatchAsync(string threadId)
        {
            AppSettings configuration; string message;
            lock (sync)
            {
                configuration = Json.Clone(settings);
                if (sender is CliSender && CliSender.Discover(configuration.CliPath) == null)
                {
                    sessions[threadId].NextRetryUtc = null;
                    sessions[threadId].Reason = "未找到 Codex CLI，请在设置中指定 codex.exe 后人工续接。";
                    return;
                }
                engine.BeginDispatch(sessions[threadId], settings, now());
                message = sessions[threadId].DispatchMessage;
                store.SaveSessions(sessions.Values);
            }
            Changed?.Invoke();
            SendResult delivery;
            try { delivery = await sender.SendAsync(configuration, threadId, message); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is ArgumentException)
            { delivery = new SendResult { Detail = "发送结果无法确认，请在 Codex 中核对；程序保留待确认状态，避免重复消息。" }; }
            lock (sync)
            {
                engine.MarkDelivery(sessions[threadId], delivery.Detail, now());
                AddEvent(sessions[threadId].Events[0]);
                store.SaveSessions(sessions.Values);
            }
        }
        public async Task ContinueAsync(string threadId)
        {
            await gate.WaitAsync();
            try
            {
                var check = await reader.ReadAsync(Settings); ApplyResult(check);
                if (!check.Available) throw new InvalidOperationException("当前无法读取会话状态，已取消发送。");
                lock (sync) if (!sessions.ContainsKey(threadId)) throw new InvalidOperationException("没有找到该会话。");
                lock (sync) if (!TargetStillFailed(check, threadId)) throw new InvalidOperationException("当前来源未确认该会话仍为原失败轮次，已取消发送。");
                await DispatchAsync(threadId);
            }
            finally { gate.Release(); Changed?.Invoke(); }
        }
        public void SetSessionMonitoring(string threadId, bool enabled)
        {
            lock (sync)
            {
                SessionRecord session; if (!sessions.TryGetValue(threadId, out session)) return;
                session.Monitoring = enabled;
                store.SaveSessions(sessions.Values);
            }
            Changed?.Invoke();
        }
        public void SetGlobalMonitoring(bool enabled)
        {
            lock (sync) { var copy = Json.Clone(settings); copy.GlobalMonitoring = enabled; settingsStore.Save(copy); settings = copy; }
            Changed?.Invoke();
        }
        public async Task SaveSettingsAsync(AppSettings value)
        {
            ValidateSettings(value);
            await gate.WaitAsync();
            try
            {
                var copy = Json.Clone(value); copy.CustomMessage = (copy.CustomMessage ?? "").Trim();
                lock (sync)
                {
                    if (!string.Equals(Path.GetFullPath(copy.CodexHome), Path.GetFullPath(settings.CodexHome), StringComparison.OrdinalIgnoreCase))
                        foreach (var session in sessions.Values)
                        {
                            session.FreshFailure = false; session.NextRetryUtc = null; session.LastEvidenceUtc = default(DateTime);
                            if (!session.AwaitingConfirmation) { session.State = SessionState.Unknown; session.Reason = "数据源已变更，等待新的执行事件。"; }
                        }
                    if (copy.StartWithWindows != settings.StartWithWindows) SetStartup(copy.StartWithWindows);
                    if (copy.Notifications.WebhookUrl != settings.Notifications.WebhookUrl || copy.Notifications.Provider != settings.Notifications.Provider || copy.Notifications.Secret != settings.Notifications.Secret)
                        store.CancelPendingNotifications();
                    settingsStore.Save(copy); settings = copy;
                    NotificationStatus = copy.Notifications.Enabled ? "通知已启用" : "通知未启用";
                }
            }
            finally { gate.Release(); Changed?.Invoke(); }
        }
        public async Task<string> TestWebhookAsync(AppSettings value)
        {
            WebhookClient.Validate(value.Notifications);
            var result = await webhooks.SendAsync(value.Notifications, new Notice { Event = "test", Title = "连接测试", State = "测试", Reason = "这是一条手动测试通知，不包含会话内容。", ThreadId = "test" });
            NotificationStatus = result.Message; Changed?.Invoke(); return result.Message;
        }
        private static void ValidateSettings(AppSettings value)
        {
            if (value == null) throw new ArgumentException("设置不能为空。");
            value.EffectiveMessage();
            if (!Directory.Exists(value.CodexHome)) throw new ArgumentException("Codex 数据目录不存在。");
            if (!string.IsNullOrWhiteSpace(value.CliPath) && !File.Exists(value.CliPath)) throw new ArgumentException("指定的 Codex CLI 文件不存在。");
            if (value.PollSeconds < 1 || value.PollSeconds > 60 || value.MaxAttempts < 1 || value.MaxAttempts > 20 || value.NetworkDelaySeconds < 1 || value.NetworkDelaySeconds > 300)
                throw new ArgumentException("轮询间隔应为 1–60 秒，续接次数 1–20 次，网络等待 1–300 秒。");
            if (value.Notifications == null) value.Notifications = new NotificationSettings();
            if (value.Notifications.Enabled || !string.IsNullOrWhiteSpace(value.Notifications.WebhookUrl)) WebhookClient.Validate(value.Notifications);
            if (value.Notifications.Enabled && !value.Notifications.OnFailure && !value.Notifications.OnRecovery && !value.Notifications.OnExhausted) throw new ArgumentException("请至少选择一种通知事件。");
        }
        private void RecordSessionEvent(SessionRecord session, MonitorEvent item)
        {
            AddEvent(item);
            var config = settings.Notifications;
            if (!config.Enabled || !(item.Kind == "failure" && config.OnFailure || item.Kind == "recovery" && config.OnRecovery || item.Kind == "exhausted" && config.OnExhausted)) return;
            var key = session.ThreadId + ":" + (session.IncidentId ?? session.FailedTurnId ?? session.TurnId) + ":" + item.Kind;
            store.Enqueue(key, new Notice { Event = item.Kind, Title = session.Title, ThreadId = session.ThreadId, State = session.State.ToString(), Reason = item.Kind == "recovery" ? "已确认新的实际执行进展。" : session.Reason, Attempts = session.Attempts, NextRetryUtc = session.NextRetryUtc });
        }
        private void AddEvent(MonitorEvent item)
        {
            events.Insert(0, item); if (events.Count > 500) events.RemoveAt(events.Count - 1); store.AppendEvent(item);
        }
        private async Task ProcessNotificationsAsync()
        {
            try
            {
                var config = Settings.Notifications;
                if (!config.Enabled || disposed) return;
                foreach (var row in store.PendingNotifications())
                {
                    if (disposed) break;
                    var active = Settings.Notifications;
                    if (!active.Enabled || Json.Serialize(active) != Json.Serialize(config)) break;
                    var notice = Json.Deserialize<Notice>((string)row["json"]);
                    if (notice == null || !(notice.Event == "failure" && active.OnFailure || notice.Event == "recovery" && active.OnRecovery || notice.Event == "exhausted" && active.OnExhausted))
                    { store.CompleteNotification((string)row["event_key"], Convert.ToInt32(row["attempts"]), true, now()); continue; }
                    DeliveryResult result;
                    try { result = await webhooks.SendAsync(config, notice).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is IOException)
                    { result = new DeliveryResult { Message = "通知配置或记录无效，投递失败。" }; }
                    var attempts = Convert.ToInt32(row["attempts"]) + 1;
                    store.CompleteNotification((string)row["event_key"], attempts, result.Success || attempts >= 3, now().AddSeconds(Math.Max(result.RetryAfterSeconds, attempts * 10)));
                    NotificationStatus = result.Message + (!result.Success && attempts >= 3 ? " 已达到 3 次投递上限。" : "");
                    if (!result.Success && attempts >= 3) lock (sync) AddEvent(new MonitorEvent { Kind = "notification", Message = NotificationStatus });
                    Changed?.Invoke();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is ArgumentException || ex is ObjectDisposedException)
            { NotificationStatus = "通知投递暂不可用，请检查设置。"; }
        }
        private bool TargetStillFailed(ReadResult result, string id)
        {
            SessionRecord session;
            return result.Available && sessions.TryGetValue(id, out session) && result.Observations.Any(x => x.ThreadId == id && x.TurnId == session.FailedTurnId && (x.Kind == EvidenceKind.Snapshot && x.IsTerminalFailure || x.Kind == EvidenceKind.TerminalFailure && x.IsFresh));
        }
        private static void SetStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled) key.SetValue("CodexAutoContinue", CliSender.Quote(System.Reflection.Assembly.GetExecutingAssembly().Location) + " --tray");
                else key.DeleteValue("CodexAutoContinue", false);
            }
        }
        public void Dispose() { disposed = true; if (notifications.IsCompleted) webhooks.Dispose(); }
    }
}

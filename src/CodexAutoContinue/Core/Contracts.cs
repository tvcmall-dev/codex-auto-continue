using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodexAutoContinue.Core
{
    public enum SessionState { Unknown, Running, InternalRetry, WaitingApproval, NetworkFailure, QuotaWait, Pending, Completed, Stopped, Failed }
    public enum EvidenceKind { Snapshot, Started, Progress, InternalRetry, TerminalFailure, Completed, Stopped, Approval, UserMessage }
    public sealed class SessionObservation
    {
        public string Id { get; set; }
        public string ThreadId { get; set; }
        public string Title { get; set; }
        public string Workspace { get; set; }
        public string TurnId { get; set; }
        public string FailureCode { get; set; }
        public string Reason { get; set; }
        public DateTime TimestampUtc { get; set; }
        public DateTime? RetryAtUtc { get; set; }
        public EvidenceKind Kind { get; set; }
        public SessionState State { get; set; }
        public bool IsFresh { get; set; }
        public bool IsContinuationMessage { get; set; }
        public bool IsTerminalFailure { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string UserMessageFingerprint { get; set; }
    }
    public static class MessageIdentity
    {
        public static string Fingerprint(string message)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes((message ?? "").Trim())));
        }
    }
    public sealed class MonitorEvent
    {
        public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
        public string ThreadId { get; set; }
        public string Kind { get; set; }
        public string Message { get; set; }
    }
    public sealed class SessionRecord
    {
        public string ThreadId { get; set; }
        public string Title { get; set; }
        public string Workspace { get; set; }
        public string TurnId { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public DateTime LastEvidenceUtc { get; set; }
        public SessionState State { get; set; }
        public string Reason { get; set; }
        public bool Monitoring { get; set; }
        public string LastObservationId { get; set; }
        public string IncidentId { get; set; }
        public string FailedTurnId { get; set; }
        public int Attempts { get; set; }
        public DateTime? NextRetryUtc { get; set; }
        public bool AwaitingConfirmation { get; set; }
        public DateTime? DispatchUtc { get; set; }
        public string DispatchMessage { get; set; }
        public string RecoveryTurnId { get; set; }
        public bool FreshFailure { get; set; }
        public List<MonitorEvent> Events { get; set; } = new List<MonitorEvent>();
    }
    public sealed class NotificationSettings
    {
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "DingTalk";
        public string Name { get; set; } = "工作提醒";
        public string WebhookUrl { get; set; } = "";
        public string Secret { get; set; } = "";
        public bool OnFailure { get; set; } = true;
        public bool OnRecovery { get; set; } = true;
        public bool OnExhausted { get; set; } = true;
    }
    public sealed class AppSettings
    {
        public string CodexHome { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        public string CliPath { get; set; } = "";
        public bool GlobalMonitoring { get; set; } = true;
        public bool StartWithWindows { get; set; }
        public bool CloseToTray { get; set; } = true;
        public bool AutoCheckUpdates { get; set; } = true;
        public bool AutoInstallUpdates { get; set; } = true;
        public bool IncludePrereleaseUpdates { get; set; } = true;
        public int PollSeconds { get; set; } = 3;
        public int MaxAttempts { get; set; } = 5;
        public int NetworkDelaySeconds { get; set; } = 30;
        public string ReplyMode { get; set; } = "zh";
        public string CustomMessage { get; set; } = "";
        public NotificationSettings Notifications { get; set; } = new NotificationSettings();
        public string EffectiveMessage()
        {
            if (ReplyMode == "zh") return "继续";
            if (ReplyMode == "en") return "continue";
            if (ReplyMode != "custom") throw new ArgumentException("请选择有效的续接消息语言。");
            var value = (CustomMessage ?? "").Trim();
            if (value.Length == 0 || value.Length > 1000) throw new ArgumentException("自定义消息不能为空，且不能超过 1000 个字符。");
            return value;
        }
    }
    public sealed class ReadResult
    {
        public bool Available { get; set; }
        public string Status { get; set; }
        public List<SessionObservation> Observations { get; set; } = new List<SessionObservation>();
    }
    public interface ISessionReader { Task<ReadResult> ReadAsync(AppSettings settings); }
    public interface IApplicationService : IDisposable
    {
        event Action Changed;
        IReadOnlyList<SessionRecord> Sessions { get; }
        IReadOnlyList<MonitorEvent> Events { get; }
        AppSettings Settings { get; }
        string ConnectionStatus { get; }
        bool IsConnected { get; }
        string NotificationStatus { get; }
        string DataDirectory { get; }
        Task RefreshAsync();
        Task ContinueAsync(string threadId);
        void SetSessionMonitoring(string threadId, bool enabled);
        void SetGlobalMonitoring(bool enabled);
        Task SaveSettingsAsync(AppSettings settings);
        Task<string> TestWebhookAsync(AppSettings settings);
    }
}

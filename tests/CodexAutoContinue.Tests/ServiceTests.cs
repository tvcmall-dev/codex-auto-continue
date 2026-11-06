using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CodexAutoContinue.Core;
using CodexAutoContinue.Infrastructure;
using CodexAutoContinue.Integration;
using CodexAutoContinue.Notifications;

namespace CodexAutoContinue.Tests
{
    public static class ServiceTests
    {
        private const string ThreadId = "00000000-0000-4000-8000-000000000001";
        public static void Run()
        {
            MissingTargetCancelsDispatch().GetAwaiter().GetResult();
            PendingDeliverySurvivesRestartWithoutDuplication().GetAwaiter().GetResult();
            ChangedWebhookStopsOldDestinationBatch().GetAwaiter().GetResult();
            QueuedEchoUsesCapturedMessageAfterSettingsChange().GetAwaiter().GetResult();
            ChangedSourceInvalidatesOldFailure().GetAwaiter().GetResult();
            InactivityOnlyMarksUnknownWithoutContinuing().GetAwaiter().GetResult();
            UnsubscribedQueuedNoticeIsDiscarded().GetAwaiter().GetResult();
        }

        private static async Task MissingTargetCancelsDispatch()
        {
            var directory = NewDirectory();
            try
            {
                var clock = DateTime.UtcNow;
                var reader = new ScriptedReader(); var sender = new RecordingSender();
                using (var service = new ApplicationService(directory, reader, sender, utcNow: () => clock))
                {
                    await Configure(service, directory);
                    await CreateScheduledFailure(service, reader, clock);
                    clock = clock.AddSeconds(2);
                    reader.Enqueue(Result(Snapshot(clock)));
                    reader.Enqueue(Result());
                    await service.RefreshAsync();
                    Require(sender.Count == 0, "The immediate pre-send read omitted the thread; it must not use the stale persisted schedule.");
                    Require(!service.Sessions.Single().FreshFailure, "A disappeared thread must lose its automatic eligibility.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static async Task PendingDeliverySurvivesRestartWithoutDuplication()
        {
            var directory = NewDirectory();
            try
            {
                var clock = DateTime.UtcNow;
                var reader = new ScriptedReader(); var sender = new RecordingSender();
                using (var service = new ApplicationService(directory, reader, sender, utcNow: () => clock))
                {
                    await Configure(service, directory);
                    await CreateScheduledFailure(service, reader, clock);
                    clock = clock.AddSeconds(2);
                    reader.Enqueue(Result(Snapshot(clock))); reader.Enqueue(Result(Snapshot(clock)));
                    await service.RefreshAsync();
                    Require(sender.Count == 1 && service.Sessions.Single().AwaitingConfirmation, "An uncertain CLI result must remain pending after one send.");
                    reader.Enqueue(Result(Snapshot(clock)));
                    await service.RefreshAsync();
                    Require(sender.Count == 1, "A repeated poll must not duplicate an unconfirmed continuation.");
                }
                var restartedReader = new ScriptedReader(); var restartedSender = new RecordingSender();
                using (var restarted = new ApplicationService(directory, restartedReader, restartedSender, utcNow: () => clock))
                {
                    restartedReader.Enqueue(Result(Snapshot(clock)));
                    await restarted.RefreshAsync();
                    Require(restartedSender.Count == 0 && restarted.Sessions.Single().AwaitingConfirmation, "Restart must preserve pending delivery and never send it again.");
                    clock = clock.AddSeconds(1);
                    restartedReader.Enqueue(Result(new SessionObservation
                    {
                        Id = "actual-progress", ThreadId = ThreadId, Title = "测试会话", TurnId = "recovery-turn",
                        TimestampUtc = clock, IsFresh = true, Kind = EvidenceKind.Progress, State = SessionState.Running
                    }));
                    await restarted.RefreshAsync();
                    Require(!restarted.Sessions.Single().AwaitingConfirmation && restarted.Sessions.Single().State == SessionState.Running,
                        "Actual new-turn execution after restart must confirm the existing continuation.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static async Task ChangedWebhookStopsOldDestinationBatch()
        {
            var directory = NewDirectory();
            var handler = new BlockingHandler();
            try
            {
                var clock = DateTime.UtcNow;
                var reader = new ScriptedReader();
                using (var client = new WebhookClient(handler))
                using (var service = new ApplicationService(directory, reader, new RecordingSender(), client, () => clock))
                {
                    await Configure(service, directory);
                    var settings = service.Settings;
                    settings.Notifications = new NotificationSettings
                    {
                        Enabled = true, Provider = "Generic", WebhookUrl = "https://first.example.invalid/hook", OnFailure = true
                    };
                    await service.SaveSettingsAsync(settings);
                    var second = Failure(clock, "00000000-0000-4000-8000-000000000002");
                    reader.Enqueue(Result(Failure(clock), second));
                    await service.RefreshAsync();
                    Require(handler.Urls.Count == 1, "The fake first notification must be blocked before destination changes.");
                    settings = service.Settings;
                    settings.Notifications.WebhookUrl = "https://second.example.invalid/hook";
                    await service.SaveSettingsAsync(settings);
                    handler.Release(false);
                    await NotificationTask(service);
                    Require(handler.Urls.Count == 1, "Remaining queued notifications must not be sent to the old destination after settings change.");
                    using (var database = new SqliteDatabase(Path.Combine(directory, "monitor.db"), true))
                        Require(database.Query("SELECT done FROM notifications").All(row => Convert.ToInt64(row["done"]) == 1),
                            "A failed in-flight result must not resurrect notifications canceled by a destination change.");
                }
            }
            finally { handler.Release(); Directory.Delete(directory, true); }
        }

        private static async Task QueuedEchoUsesCapturedMessageAfterSettingsChange()
        {
            var directory = NewDirectory();
            try
            {
                var clock = DateTime.UtcNow;
                var reader = new ScriptedReader(); var sender = new RecordingSender();
                using (var service = new ApplicationService(directory, reader, sender, utcNow: () => clock))
                {
                    await Configure(service, directory);
                    await CreateScheduledFailure(service, reader, clock);
                    clock = clock.AddSeconds(2);
                    reader.Enqueue(Result(Snapshot(clock))); reader.Enqueue(Result(Snapshot(clock)));
                    await service.RefreshAsync();
                    Require(sender.Count == 1 && service.Sessions.Single().DispatchMessage == "继续", "Fixture continuation must capture the original Chinese message.");
                    var settings = service.Settings; settings.ReplyMode = "en";
                    await service.SaveSettingsAsync(settings);
                    clock = clock.AddSeconds(1);
                    var repeatedFailure = Failure(clock); repeatedFailure.Id = "continued-failure"; repeatedFailure.TurnId = "resumed-turn";
                    reader.Enqueue(Result(new SessionObservation
                    {
                        Id = "queued-user-echo", ThreadId = ThreadId, Title = "测试会话", TurnId = "resumed-turn",
                        TimestampUtc = clock, IsFresh = true, Kind = EvidenceKind.UserMessage, State = SessionState.Unknown,
                        IsContinuationMessage = false, UserMessageFingerprint = MessageIdentity.Fingerprint("继续")
                    }, repeatedFailure));
                    await service.RefreshAsync();
                    var session = service.Sessions.Single();
                    Require(session.Attempts == 1 && !session.AwaitingConfirmation && session.NextRetryUtc.HasValue,
                        "Changing the saved language must not turn an already queued continuation echo into a new task or reset its retry budget.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static async Task ChangedSourceInvalidatesOldFailure()
        {
            var directory = NewDirectory();
            try
            {
                var clock = DateTime.UtcNow;
                var reader = new ScriptedReader(); var sender = new RecordingSender();
                using (var service = new ApplicationService(directory, reader, sender, utcNow: () => clock))
                {
                    await Configure(service, directory);
                    await CreateScheduledFailure(service, reader, clock);
                    var newSource = Path.Combine(directory, "another-source"); Directory.CreateDirectory(newSource);
                    var settings = service.Settings; settings.CodexHome = newSource;
                    await service.SaveSettingsAsync(settings);
                    Require(!service.Sessions.Single().FreshFailure && !service.Sessions.Single().NextRetryUtc.HasValue,
                        "Changing Codex home must immediately invalidate an old source's retry schedule.");
                    clock = clock.AddSeconds(2);
                    reader.Enqueue(Result(Snapshot(clock)));
                    await service.RefreshAsync();
                    Require(sender.Count == 0, "A copied historical failure with the same IDs in a new source must not reuse old eligibility.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static async Task InactivityOnlyMarksUnknownWithoutContinuing()
        {
            var directory = NewDirectory();
            try
            {
                var clock = DateTime.UtcNow;
                var reader = new ScriptedReader(); var sender = new RecordingSender();
                using (var service = new ApplicationService(directory, reader, sender, utcNow: () => clock))
                {
                    await Configure(service, directory);
                    reader.Enqueue(Result(new SessionObservation { Id = "live-progress", ThreadId = ThreadId, TurnId = "running-turn", TimestampUtc = clock, IsFresh = true, Kind = EvidenceKind.Progress, State = SessionState.Running }));
                    await service.RefreshAsync(); service.SetSessionMonitoring(ThreadId, true);
                    Require(service.Sessions.Single().State == SessionState.Running, "Actual new execution must initially show running.");
                    clock = clock.AddMinutes(2).AddSeconds(1);
                    reader.Enqueue(Result(new SessionObservation { Id = "unchanged-history", ThreadId = ThreadId, TurnId = "running-turn", TimestampUtc = clock, Kind = EvidenceKind.Snapshot, State = SessionState.Unknown }));
                    await service.RefreshAsync();
                    var stale = service.Sessions.Single();
                    Require(stale.State == SessionState.Unknown && stale.Reason.Contains("2 分钟"), "Without new evidence for two minutes the UI must mark runtime unknown.");
                    Require(sender.Count == 0 && !stale.NextRetryUtc.HasValue && !stale.FreshFailure, "Inactivity alone must never enable or send a continuation, even for a monitored thread.");
                    clock = clock.AddSeconds(1);
                    reader.Enqueue(Result(new SessionObservation { Id = "live-progress-again", ThreadId = ThreadId, TurnId = "running-turn", TimestampUtc = clock, IsFresh = true, Kind = EvidenceKind.Progress, State = SessionState.Running }));
                    await service.RefreshAsync();
                    Require(service.Sessions.Single().State == SessionState.Running && sender.Count == 0, "New real progress must restore the running display without sending a message.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static async Task UnsubscribedQueuedNoticeIsDiscarded()
        {
            var directory = NewDirectory();
            try
            {
                var reader = new ScriptedReader(); var handler = new RecordingHandler();
                using (var client = new WebhookClient(handler))
                using (var service = new ApplicationService(directory, reader, new RecordingSender(), client))
                {
                    await Configure(service, directory);
                    var settings = service.Settings;
                    settings.Notifications = new NotificationSettings { Enabled = true, Provider = "Generic", WebhookUrl = "https://example.invalid/hook", OnFailure = true, OnRecovery = true };
                    await service.SaveSettingsAsync(settings);
                    new StateStore(directory).Enqueue("previously-subscribed", new Notice { Event = "failure", Title = "测试会话", ThreadId = ThreadId });
                    settings = service.Settings; settings.Notifications.OnFailure = false;
                    await service.SaveSettingsAsync(settings);
                    reader.Enqueue(Result()); await service.RefreshAsync();
                    await NotificationTask(service);
                    Require(handler.Count == 0, "Queued events that are no longer subscribed must not be transmitted.");
                    using (var database = new SqliteDatabase(Path.Combine(directory, "monitor.db"), true))
                        Require(Convert.ToInt64(database.Query("SELECT done FROM notifications WHERE event_key=?", "previously-subscribed").Single()["done"]) == 1,
                            "Discarded unsubscribed events must be marked complete rather than retried forever.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        private static async Task Configure(ApplicationService service, string directory)
        {
            var settings = service.Settings;
            settings.CodexHome = directory; settings.NetworkDelaySeconds = 1; settings.MaxAttempts = 3;
            settings.GlobalMonitoring = true; settings.StartWithWindows = false;
            await service.SaveSettingsAsync(settings);
        }
        private static async Task CreateScheduledFailure(ApplicationService service, ScriptedReader reader, DateTime now)
        {
            reader.Enqueue(Result(Snapshot(now.AddSeconds(-1))));
            await service.RefreshAsync(); service.SetSessionMonitoring(ThreadId, true);
            reader.Enqueue(Result(Failure(now)));
            await service.RefreshAsync();
            Require(service.Sessions.Single().NextRetryUtc.HasValue, "Fixture network failure must have a scheduled continuation.");
        }
        private static SessionObservation Snapshot(DateTime time)
        {
            return new SessionObservation { Id = "snapshot-" + time.Ticks, ThreadId = ThreadId, Title = "测试会话", TurnId = "failed-turn", TimestampUtc = time, Kind = EvidenceKind.Snapshot, State = SessionState.Unknown, IsTerminalFailure = true };
        }
        private static SessionObservation Failure(DateTime time, string threadId = ThreadId)
        {
            return new SessionObservation { Id = "failure-" + threadId, ThreadId = threadId, Title = "测试会话", TurnId = "failed-turn", TimestampUtc = time, Kind = EvidenceKind.TerminalFailure, State = SessionState.Failed, IsFresh = true, FailureCode = "network", Reason = "网络连接中断。" };
        }
        private static ReadResult Result(params SessionObservation[] observations) { return new ReadResult { Available = true, Status = "测试本地投影", Observations = observations.ToList() }; }
        private static string NewDirectory() { var path = Path.Combine(Path.GetTempPath(), "CodexAutoContinue-service-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        private static Task NotificationTask(ApplicationService service)
        {
            return (Task)typeof(ApplicationService).GetField("notifications", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

        private sealed class ScriptedReader : ISessionReader
        {
            private readonly Queue<ReadResult> results = new Queue<ReadResult>();
            private ReadResult last = Result();
            public void Enqueue(ReadResult result) { results.Enqueue(result); }
            public Task<ReadResult> ReadAsync(AppSettings settings) { if (results.Count > 0) last = results.Dequeue(); return Task.FromResult(last); }
        }
        private sealed class RecordingSender : ICliSender
        {
            public int Count;
            public Task<SendResult> SendAsync(AppSettings settings, string threadId, string message)
            {
                Count++; return Task.FromResult(new SendResult { Detail = "测试：消息入队结果待确认。" });
            }
        }
        private sealed class BlockingHandler : HttpMessageHandler
        {
            public readonly List<string> Urls = new List<string>();
            private readonly TaskCompletionSource<HttpResponseMessage> first = new TaskCompletionSource<HttpResponseMessage>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Urls.Add(request.RequestUri.AbsoluteUri);
                return Urls.Count == 1 ? first.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }
            public void Release(bool success = true) { first.TrySetResult(new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }); }
        }
        private sealed class RecordingHandler : HttpMessageHandler
        {
            public int Count;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }
        }
    }
}

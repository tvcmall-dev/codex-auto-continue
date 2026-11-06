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
using CodexAutoContinue.Updating;

namespace CodexAutoContinue.Tests
{
    public static class UpdateHostTests
    {
        private const string ThreadId = "00000000-0000-4000-8000-000000000011";
        public static void Run()
        {
            MaintenancePreservesPendingAndBlocksNewWork().GetAwaiter().GetResult();
            SenderInFlightDefersUpdate().GetAwaiter().GetResult();
            ManualWebhookInFlightDefersUpdate().GetAwaiter().GetResult();
            NotificationInFlightDefersUpdate().GetAwaiter().GetResult();
            ImminentContinuationDefersUpdate().GetAwaiter().GetResult();
            FutureContinuationDefersUpdate("network").GetAwaiter().GetResult();
            FutureContinuationDefersUpdate("rateLimit").GetAwaiter().GetResult();
        }
        private static async Task MaintenancePreservesPendingAndBlocksNewWork()
        {
            var directory = NewDirectory();
            try
            {
                new StateStore(directory).SaveSessions(new[] { new SessionRecord { ThreadId = ThreadId, State = SessionState.Pending, Monitoring = true, AwaitingConfirmation = true, Attempts = 2, IncidentId = "original-incident", FailedTurnId = "failed", DispatchUtc = DateTime.UtcNow, DispatchMessage = "继续" } });
                var reader = new Reader(); var handler = new Handler();
                using (var client = new WebhookClient(handler))
                using (var service = new ApplicationService(directory, reader, new Sender(), client))
                {
                    await Configure(service, directory);
                    var host = Host(service);
                    Require(await host.TryPauseForUpdateAsync(), "An idle host must pause for update.");
                    var current = service.Sessions.Single();
                    Require(current.AwaitingConfirmation && current.Attempts == 2 && current.IncidentId == "original-incident", "Pausing must preserve unconfirmed dispatch and its budget.");
                    Require(service.Settings.GlobalMonitoring, "Maintenance must not change the user's global preference.");
                    var reads = reader.Count;
                    await service.RefreshAsync();
                    Require(reader.Count == reads, "Polling must stop while replacement is prepared.");
                    await UpdatingError(() => service.ContinueAsync(ThreadId));
                    await UpdatingError(() => service.SaveSettingsAsync(service.Settings));
                    await UpdatingError(() => service.TestWebhookAsync(WebhookSettings(service)));
                    UpdatingError(() => service.SetSessionMonitoring(ThreadId, false));
                    UpdatingError(() => service.SetGlobalMonitoring(false));
                    Require(handler.Count == 0, "Maintenance must not start a manual webhook test.");
                    var saved = new StateStore(directory).LoadSessions().Single();
                    Require(saved.AwaitingConfirmation && saved.Attempts == 2, "Pause must persist the pending record before allowing shutdown.");
                    host.ResumeAfterUpdateFailure();
                    await service.RefreshAsync();
                    Require(reader.Count > reads, "Failed update preparation must allow polling to resume.");
                    service.SetGlobalMonitoring(false);
                    Require(!service.Settings.GlobalMonitoring, "Settings writes must resume after maintenance ends.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        private static async Task SenderInFlightDefersUpdate()
        {
            var directory = NewDirectory(); var sender = new Sender { Block = true };
            try
            {
                var clock = DateTime.UtcNow; var reader = new Reader();
                using (var service = new ApplicationService(directory, reader, sender, utcNow: () => clock))
                {
                    await Configure(service, directory);
                    await Failure(service, reader, clock, 60);
                    reader.Next = Snapshot(clock.AddSeconds(1));
                    var dispatch = service.ContinueAsync(ThreadId);
                    Require(sender.Count == 1, "Fixture sender must be active.");
                    Require(!await Host(service).TryPauseForUpdateAsync(), "An in-flight continuation must defer updating.");
                    sender.Release(); await dispatch;
                    Require(await Host(service).TryPauseForUpdateAsync(), "A persisted pending result may safely survive updater restart.");
                }
            }
            finally { sender.Release(); Directory.Delete(directory, true); }
        }
        private static async Task ManualWebhookInFlightDefersUpdate()
        {
            var directory = NewDirectory(); var handler = new Handler { Block = true };
            try
            {
                using (var client = new WebhookClient(handler))
                using (var service = new ApplicationService(directory, new Reader(), new Sender(), client))
                {
                    await Configure(service, directory);
                    var testing = service.TestWebhookAsync(WebhookSettings(service));
                    Require(handler.Count == 1, "Fixture manual webhook must be active.");
                    Require(!await Host(service).TryPauseForUpdateAsync(), "A manual webhook test must defer restart even without the polling gate.");
                    handler.Release(); await testing;
                    Require(await Host(service).TryPauseForUpdateAsync(), "The host becomes idle after the manual test completes.");
                }
            }
            finally { handler.Release(); Directory.Delete(directory, true); }
        }
        private static async Task NotificationInFlightDefersUpdate()
        {
            var directory = NewDirectory(); var handler = new Handler { Block = true };
            try
            {
                using (var client = new WebhookClient(handler))
                using (var service = new ApplicationService(directory, new Reader(), new Sender(), client))
                {
                    await Configure(service, directory);
                    var settings = WebhookSettings(service); settings.Notifications.Enabled = true;
                    await service.SaveSettingsAsync(settings);
                    new StateStore(directory).Enqueue("notice", new Notice { Event = "failure", Title = "测试会话", ThreadId = ThreadId });
                    await service.RefreshAsync();
                    Require(handler.Count == 1, "Fixture queued webhook must be active.");
                    Require(!await Host(service).TryPauseForUpdateAsync(), "Background notification delivery must finish before restart.");
                    handler.Release();
                    await (Task)typeof(ApplicationService).GetField("notifications", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
                    Require(await Host(service).TryPauseForUpdateAsync(), "The host becomes idle after queued delivery.");
                }
            }
            finally { handler.Release(); Directory.Delete(directory, true); }
        }
        private static async Task ImminentContinuationDefersUpdate()
        {
            var directory = NewDirectory();
            try
            {
                var clock = DateTime.UtcNow; var reader = new Reader();
                using (var service = new ApplicationService(directory, reader, new Sender(), utcNow: () => clock))
                {
                    await Configure(service, directory);
                    await Failure(service, reader, clock, 20);
                    Require(!await Host(service).TryPauseForUpdateAsync(), "A monitored eligible continuation due within 30 seconds must not be interrupted by an update.");
                    service.SetGlobalMonitoring(false);
                    Require(await Host(service).TryPauseForUpdateAsync(), "A globally paused schedule does not block an otherwise idle update.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        private static async Task FutureContinuationDefersUpdate(string failureCode)
        {
            var directory = NewDirectory();
            try
            {
                var clock = DateTime.UtcNow; var reader = new Reader();
                using (var service = new ApplicationService(directory, reader, new Sender(), utcNow: () => clock))
                {
                    await Configure(service, directory);
                    await Failure(service, reader, clock, 120, failureCode);
                    var session = service.Sessions.Single();
                    Require(session.FreshFailure && session.NextRetryUtc > clock.AddSeconds(30), "Fixture must have a pending recovery scheduled beyond the immediate idle window.");
                    Require(!await Host(service).TryPauseForUpdateAsync(), "A future enabled recovery plan must finish before restart so its schedule is not lost on startup.");
                    service.SetSessionMonitoring(ThreadId, false);
                    Require(await Host(service).TryPauseForUpdateAsync(), "An explicitly disabled session does not block an otherwise idle update.");
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        private static IUpdateHost Host(ApplicationService service)
        {
            var host = (object)service as IUpdateHost;
            Require(host != null, "ApplicationService must expose an update quiesce host.");
            return host;
        }
        private static async Task Configure(ApplicationService service, string directory)
        {
            var settings = service.Settings; settings.CodexHome = directory;
            await service.SaveSettingsAsync(settings);
        }
        private static AppSettings WebhookSettings(ApplicationService service)
        {
            var settings = service.Settings;
            settings.Notifications = new NotificationSettings { Provider = "Generic", WebhookUrl = "https://example.invalid/hook" };
            return settings;
        }
        private static async Task Failure(ApplicationService service, Reader reader, DateTime clock, int delay, string failureCode = "network")
        {
            var settings = service.Settings; settings.NetworkDelaySeconds = delay;
            await service.SaveSettingsAsync(settings);
            reader.Next = Snapshot(clock.AddSeconds(-1)); await service.RefreshAsync();
            service.SetSessionMonitoring(ThreadId, true);
            reader.Next = new ReadResult { Available = true, Observations = new List<SessionObservation> { new SessionObservation { ThreadId = ThreadId, Id = "failure", TurnId = "failed", Kind = EvidenceKind.TerminalFailure, IsFresh = true, TimestampUtc = clock, FailureCode = failureCode, RetryAtUtc = failureCode == "rateLimit" ? (DateTime?)clock.AddSeconds(delay) : null, State = SessionState.Failed } } };
            await service.RefreshAsync();
        }
        private static ReadResult Snapshot(DateTime time)
        {
            return new ReadResult { Available = true, Observations = new List<SessionObservation> { new SessionObservation { ThreadId = ThreadId, Id = "snapshot-" + time.Ticks, TurnId = "failed", Kind = EvidenceKind.Snapshot, TimestampUtc = time, State = SessionState.Unknown, IsTerminalFailure = true } } };
        }
        private static async Task UpdatingError(Func<Task> action)
        {
            try { await action(); } catch (InvalidOperationException error) { Require(error.Message.Contains("升级"), "Maintenance must explain why work is blocked."); return; }
            throw new Exception("An operation was accepted while updating.");
        }
        private static void UpdatingError(Action action)
        {
            try { action(); } catch (InvalidOperationException error) { Require(error.Message.Contains("升级"), "Maintenance must explain why work is blocked."); return; }
            throw new Exception("An operation was accepted while updating.");
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static string NewDirectory() { var directory = Path.Combine(Path.GetTempPath(), "CodexAutoContinue-update-host-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }
        private sealed class Reader : ISessionReader
        {
            public int Count;
            public ReadResult Next = new ReadResult { Available = true };
            public Task<ReadResult> ReadAsync(AppSettings settings) { Count++; return Task.FromResult(Next); }
        }
        private sealed class Sender : ICliSender
        {
            public bool Block; public int Count;
            private readonly TaskCompletionSource<SendResult> completion = new TaskCompletionSource<SendResult>();
            public Task<SendResult> SendAsync(AppSettings settings, string threadId, string message) { Count++; return Block ? completion.Task : Task.FromResult(new SendResult { Detail = "待确认" }); }
            public void Release() { completion.TrySetResult(new SendResult { Detail = "待确认" }); }
        }
        private sealed class Handler : HttpMessageHandler
        {
            public bool Block; public int Count;
            private readonly TaskCompletionSource<HttpResponseMessage> completion = new TaskCompletionSource<HttpResponseMessage>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            { Count++; return Block ? completion.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }); }
            public void Release() { completion.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }); }
        }
    }
}

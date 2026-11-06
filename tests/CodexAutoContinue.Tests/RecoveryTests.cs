using System;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.Tests
{
    public static class RecoveryTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        private static int _checks;

        public static void Run()
        {
            _checks = 0;
            LanguageChoices();
            Classifications();
            FreshFailureAndOptIn();
            HistoricalFailureCannotDispatch();
            InternalRetryCannotDispatch();
            ProtectedStatesCancelScheduling();
            DispatchPersistsIntentAndMessage();
            PendingRequiresExecutionEvidence();
            CompletionAfterDispatchConfirmsRecovery();
            RepeatedAutomaticFailureKeepsBudget();
            DuplicateAndStaleEvidenceAreIgnored();
            OldTurnProgressCannotUndoTerminalFailure();
            NewUserTaskCancelsIncident();
            ContinuationEchoIsNotRecovery();
            UserInputCancelsUncertainDelivery();
            PendingSurvivesReconciliation();
            SnapshotDoesNotInventRecovery();
            SnapshotMetadataCannotSuppressTerminalEvidence();
            SnapshotCannotDowngradeObservedExecution();
            SameSecondCompletionCanConfirmRecovery();
            RestartReconcilesVerifiedTerminalEvidence();
            ExplicitRetryTimingTakesPrecedence();
            Console.WriteLine("Recovery policy checks passed: " + _checks);
        }

        private static AppSettings Settings() { return new AppSettings { NetworkDelaySeconds = 30, MaxAttempts = 5 }; }
        private static SessionRecord Session() { return new SessionRecord { ThreadId = "thread", Title = "test", Monitoring = true }; }
        private static SessionObservation Observe(EvidenceKind kind, string turn, DateTime time, string id = null)
        {
            return new SessionObservation { Id = id ?? Guid.NewGuid().ToString("N"), ThreadId = "thread", TurnId = turn,
                Title = "test", IsFresh = true, Kind = kind, TimestampUtc = time, FailureCode = "network", Reason = "网络连接中断" };
        }
        private static SessionRecord Failed(RecoveryEngine engine, AppSettings cfg)
        {
            var s = Session();
            engine.Apply(s, Observe(EvidenceKind.TerminalFailure, "failed-1", Now), cfg, Now);
            return s;
        }
        private static void Check(bool condition, string message)
        {
            _checks++;
            if (!condition) throw new Exception("Recovery policy: " + message);
        }
        private static void LanguageChoices()
        {
            var cfg = Settings();
            Check(cfg.EffectiveMessage() == "继续", "Chinese must be the default continuation");
            cfg.ReplyMode = "en";
            Check(cfg.EffectiveMessage() == "continue", "English must preserve exact content");
            cfg.ReplyMode = "custom"; cfg.CustomMessage = "  finish and verify  ";
            Check(cfg.EffectiveMessage() == "finish and verify", "Custom content must be trimmed");
            cfg.CustomMessage = " \t ";
            var rejected = false;
            try { cfg.EffectiveMessage(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Blank custom content must be rejected");
            cfg.CustomMessage = new string('x', 1001); rejected = false;
            try { cfg.EffectiveMessage(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Custom messages above the size limit must be rejected");
        }
        private static void Classifications()
        {
            Check(FailureClassifier.Classify("network", "", null) == SessionState.NetworkFailure, "Normalized network failures retry");
            Check(FailureClassifier.Classify("serverOverloaded", "", null) == SessionState.NetworkFailure, "Server overload retries");
            Check(FailureClassifier.Classify("rateLimit", "429", null) == SessionState.Failed, "Untimed 429 needs attention");
            Check(FailureClassifier.Classify("usageLimit", "", Now.AddHours(1)) == SessionState.QuotaWait, "Explicit quota timing is retained");
            Check(FailureClassifier.Classify("httpConnectionFailed", "402", null) == SessionState.Failed, "Payment errors never retry");
            Check(FailureClassifier.Classify("httpConnectionFailed", "407", null) == SessionState.Failed, "Proxy authentication errors never retry");
            Check(FailureClassifier.Classify("network", "409", null) == SessionState.Failed, "Other client-side HTTP failures need attention");
            Check(FailureClassifier.Classify("unauthorized", "network", null) == SessionState.Failed, "Authentication errors override network wording");
            Check(FailureClassifier.Classify("modelNotFound", "", null) == SessionState.Failed, "Missing model needs attention");
            Check(FailureClassifier.Classify("other", "unknown error", null) == SessionState.Failed, "Unknown failures do not retry");
        }
        private static void FreshFailureAndOptIn()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            Check(s.State == SessionState.NetworkFailure && s.FreshFailure, "Fresh terminal network failure is eligible");
            Check(!string.IsNullOrEmpty(s.IncidentId) && s.FailedTurnId == "failed-1", "Failure has a durable incident identity");
            Check(s.NextRetryUtc == Now.AddSeconds(30), "First retry uses the configured delay");
            Check(!e.CanDispatch(s, cfg, Now.AddSeconds(29)), "Retry cannot occur before its due time");
            Check(e.CanDispatch(s, cfg, Now.AddSeconds(30)), "Due monitored failure can dispatch");
            s.Monitoring = false;
            Check(!e.CanDispatch(s, cfg, Now.AddMinutes(1)), "Per-session opt-out prevents dispatch");
            s.Monitoring = true; cfg.GlobalMonitoring = false;
            Check(!e.CanDispatch(s, cfg, Now.AddMinutes(1)), "Global pause prevents dispatch");
            Check(!new SessionRecord().Monitoring, "New sessions are opt-in");
        }
        private static void HistoricalFailureCannotDispatch()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Session();
            var o = Observe(EvidenceKind.TerminalFailure, "old", Now.AddDays(-1)); o.IsFresh = false;
            e.Apply(s, o, cfg, Now);
            Check(!s.FreshFailure && !s.NextRetryUtc.HasValue, "Historical failure must not schedule continuation");
            Check(!e.CanDispatch(s, cfg, Now.AddDays(1)), "Historical failure cannot become eligible merely by time passing");
        }
        private static void InternalRetryCannotDispatch()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.Apply(s, Observe(EvidenceKind.InternalRetry, "failed-1", Now.AddSeconds(1)), cfg, Now.AddSeconds(1));
            Check(s.State == SessionState.InternalRetry && !s.NextRetryUtc.HasValue, "Codex internal retry cancels outside scheduling");
            Check(!e.CanDispatch(s, cfg, Now.AddMinutes(1)), "Internal retries cannot receive another continuation");
        }
        private static void ProtectedStatesCancelScheduling()
        {
            foreach (var kind in new[] { EvidenceKind.Stopped, EvidenceKind.Approval, EvidenceKind.Completed })
            {
                var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
                e.Apply(s, Observe(kind, "failed-1", Now.AddSeconds(1)), cfg, Now.AddSeconds(1));
                Check(!s.NextRetryUtc.HasValue && !s.AwaitingConfirmation, kind + " cancels pending actions");
                Check(!e.CanDispatch(s, cfg, Now.AddMinutes(1)), kind + " cannot auto-continue");
            }
        }
        private static void DispatchPersistsIntentAndMessage()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg); cfg.ReplyMode = "en";
            e.BeginDispatch(s, cfg, Now.AddSeconds(30));
            Check(s.State == SessionState.Pending && s.AwaitingConfirmation, "Dispatch intent enters pending before transport");
            Check(s.Attempts == 1 && s.DispatchMessage == "continue", "Dispatch stores the exact effective message and budget");
            Check(s.DispatchUtc == Now.AddSeconds(30) && !s.NextRetryUtc.HasValue, "Dispatch has a durable timestamp and no duplicate timer");
            e.MarkDelivery(s, "resume warning", Now.AddSeconds(31));
            Check(s.State == SessionState.Pending && s.AwaitingConfirmation, "CLI warning remains uncertain pending");
            Check(!e.CanDispatch(s, cfg, Now.AddDays(1)), "Pending delivery cannot be resent automatically");
        }
        private static void PendingRequiresExecutionEvidence()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.BeginDispatch(s, cfg, Now.AddSeconds(30));
            e.Apply(s, Observe(EvidenceKind.Started, "retry-1", Now.AddSeconds(31)), cfg, Now.AddSeconds(31));
            Check(s.State == SessionState.Pending && s.AwaitingConfirmation, "Turn start alone is not confirmed recovery");
            e.Apply(s, Observe(EvidenceKind.Progress, "failed-1", Now.AddSeconds(32)), cfg, Now.AddSeconds(32));
            Check(s.AwaitingConfirmation, "Output from the original failed turn is not recovery");
            e.Apply(s, Observe(EvidenceKind.Progress, "retry-1", Now.AddSeconds(33)), cfg, Now.AddSeconds(33));
            Check(s.State == SessionState.Running && !s.AwaitingConfirmation, "New-turn execution confirms recovery");
            Check(s.IncidentId == null && s.Attempts == 0, "Confirmed recovery clears incident budget");
            Check(s.Events.Exists(x => x.Kind == "recovery"), "Confirmed recovery emits a recovery event");
        }
        private static void CompletionAfterDispatchConfirmsRecovery()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.BeginDispatch(s, cfg, Now.AddSeconds(30));
            e.Apply(s, Observe(EvidenceKind.Completed, "retry-1", Now.AddSeconds(31)), cfg, Now.AddSeconds(31));
            Check(s.State == SessionState.Completed && s.Events.Exists(x => x.Kind == "recovery"), "Successful new-turn completion is recovery evidence");
        }
        private static void RepeatedAutomaticFailureKeepsBudget()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg); var incident = s.IncidentId;
            var clock = Now;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                clock = s.NextRetryUtc.Value;
                Check(e.CanDispatch(s, cfg, clock), "Retry within budget is due");
                e.BeginDispatch(s, cfg, clock);
                clock = clock.AddSeconds(1);
                e.Apply(s, Observe(EvidenceKind.TerminalFailure, "retry-" + attempt, clock), cfg, clock);
                Check(s.IncidentId == incident && s.Attempts == attempt, "Failed continuation retains original incident budget");
            }
            Check(s.State == SessionState.Failed && !s.NextRetryUtc.HasValue, "Exhausted incident stops scheduling");
            Check(!e.CanDispatch(s, cfg, clock.AddDays(1)), "Exhausted incident never silently gets a fresh budget");
            Check(s.Events.FindAll(x => x.Kind == "exhausted").Count == 1, "Exhaustion notification is deduplicated");
        }
        private static void DuplicateAndStaleEvidenceAreIgnored()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Session(); var o = Observe(EvidenceKind.TerminalFailure, "first", Now, "same");
            e.Apply(s, o, cfg, Now); var count = s.Events.Count; var due = s.NextRetryUtc;
            e.Apply(s, o, cfg, Now.AddSeconds(5));
            Check(s.Events.Count == count && s.NextRetryUtc == due, "Duplicate failure cannot restart countdown or notify twice");
            e.Apply(s, Observe(EvidenceKind.Stopped, "older", Now.AddSeconds(-1)), cfg, Now.AddSeconds(10));
            Check(s.State == SessionState.NetworkFailure, "Older observations do not overwrite newer state");
        }
        private static void NewUserTaskCancelsIncident()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.Apply(s, Observe(EvidenceKind.UserMessage, "manual-new", Now.AddSeconds(2)), cfg, Now.AddSeconds(2));
            Check(s.IncidentId == null && !s.NextRetryUtc.HasValue && !s.FreshFailure, "New user task cancels previous incident");
            Check(!e.CanDispatch(s, cfg, Now.AddHours(1)), "A new user task cannot inherit an old scheduled continuation");
        }
        private static void OldTurnProgressCannotUndoTerminalFailure()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg); var due = s.NextRetryUtc;
            e.Apply(s, Observe(EvidenceKind.Progress, "failed-1", Now.AddSeconds(1)), cfg, Now.AddSeconds(1));
            Check(s.State == SessionState.NetworkFailure && s.NextRetryUtc == due,
                "Late item writes from a terminal failed turn cannot restore the running illustration");
            e.Apply(s, Observe(EvidenceKind.Started, "failed-1", Now.AddSeconds(2)), cfg, Now.AddSeconds(2));
            Check(s.State == SessionState.NetworkFailure && s.NextRetryUtc == due,
                "A late start event from the same failed turn cannot cancel its failure incident");
        }
        private static void SnapshotDoesNotInventRecovery()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.BeginDispatch(s, cfg, Now.AddSeconds(30));
            var o = Observe(EvidenceKind.Snapshot, "retry-1", Now.AddSeconds(31)); o.State = SessionState.Running; o.IsFresh = false;
            e.Apply(s, o, cfg, Now.AddSeconds(31));
            Check(s.State == SessionState.Pending && s.AwaitingConfirmation, "Saved in-progress status cannot confirm recovery");
            Check(!s.Events.Exists(x => x.Kind == "recovery"), "Snapshot never emits recovery notification");
        }
        private static void ContinuationEchoIsNotRecovery()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.BeginDispatch(s, cfg, Now.AddSeconds(30));
            var o = Observe(EvidenceKind.UserMessage, "retry-1", Now.AddSeconds(31)); o.IsContinuationMessage = true;
            e.Apply(s, o, cfg, Now.AddSeconds(31));
            Check(s.AwaitingConfirmation && s.State == SessionState.Pending, "Queued continuation echo does not confirm recovery");
            Check(s.Attempts == 1 && s.IncidentId != null, "Echo preserves the pending incident");
        }
        private static void UserInputCancelsUncertainDelivery()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.BeginDispatch(s, cfg, Now.AddSeconds(30));
            e.Apply(s, Observe(EvidenceKind.UserMessage, "manual-new", Now.AddSeconds(31)), cfg, Now.AddSeconds(31));
            Check(!s.AwaitingConfirmation && s.IncidentId == null && !s.NextRetryUtc.HasValue,
                "A new user-authored task cancels a pending automatic action");
            Check(!s.Events.Exists(x => x.Kind == "recovery"), "User input alone never reports recovered execution");
        }
        private static void PendingSurvivesReconciliation()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.BeginDispatch(s, cfg, Now.AddSeconds(30));
            s.FreshFailure = false; s.NextRetryUtc = null;
            var o = Observe(EvidenceKind.TerminalFailure, "failed-1", Now.AddSeconds(31)); o.IsFresh = false;
            e.Apply(s, o, cfg, Now.AddSeconds(31));
            Check(s.AwaitingConfirmation && s.Attempts == 1 && s.State == SessionState.Pending,
                "Restart reconciliation preserves uncertain dispatch and the spent attempt");
            Check(!e.CanDispatch(s, cfg, Now.AddHours(1)), "Restart cannot resend an uncertain dispatch");
        }
        private static void ExplicitRetryTimingTakesPrecedence()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Session();
            var o = Observe(EvidenceKind.TerminalFailure, "quota", Now); o.FailureCode = "usageLimit"; o.RetryAtUtc = Now.AddHours(2);
            e.Apply(s, o, cfg, Now);
            Check(s.State == SessionState.QuotaWait && s.NextRetryUtc == Now.AddHours(2), "Verified upstream quota reset controls due time");
            Check(!e.CanDispatch(s, cfg, Now.AddHours(1)), "Quota wait cannot dispatch early");
        }
        private static void SnapshotMetadataCannotSuppressTerminalEvidence()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Session();
            var metadata = Observe(EvidenceKind.Snapshot, "failed-1", Now.AddMilliseconds(900));
            metadata.IsFresh = false; metadata.State = SessionState.Unknown;
            e.Apply(s, metadata, cfg, Now.AddSeconds(1));
            e.Apply(s, Observe(EvidenceKind.TerminalFailure, "failed-1", Now), cfg, Now.AddSeconds(1));
            Check(s.State == SessionState.NetworkFailure && s.FreshFailure,
                "Metadata timestamps cannot suppress fresh coarse-grained terminal evidence");
            Check(s.LastActivityUtc == Now.AddMilliseconds(900) && s.LastEvidenceUtc == Now,
                "Display activity and authoritative execution evidence have separate clocks");
        }
        private static void SameSecondCompletionCanConfirmRecovery()
        {
            var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
            e.BeginDispatch(s, cfg, Now.AddSeconds(30).AddMilliseconds(750));
            e.Apply(s, Observe(EvidenceKind.Completed, "retry-1", Now.AddSeconds(30)), cfg, Now.AddSeconds(31));
            Check(s.State == SessionState.Completed && !s.AwaitingConfirmation && s.Events.Exists(x => x.Kind == "recovery"),
                "Fresh different-turn completion recorded in Unix seconds can confirm a same-second dispatch");
        }
        private static void SnapshotCannotDowngradeObservedExecution()
        {
            foreach (var kind in new[] { EvidenceKind.Progress, EvidenceKind.InternalRetry, EvidenceKind.Approval })
            {
                var e = new RecoveryEngine(); var cfg = Settings(); var s = Session();
                e.Apply(s, Observe(kind, "active", Now), cfg, Now);
                var observedState = s.State;
                var snapshot = Observe(EvidenceKind.Snapshot, "active", Now.AddSeconds(3));
                snapshot.IsFresh = false; snapshot.State = SessionState.Unknown;
                e.Apply(s, snapshot, cfg, Now.AddSeconds(3));
                Check(s.State == observedState, "Unknown same-turn projection cannot downgrade observed " + kind);
            }
        }
        private static void RestartReconcilesVerifiedTerminalEvidence()
        {
            foreach (var state in new[] { SessionState.Completed, SessionState.Stopped, SessionState.Failed })
            {
                var e = new RecoveryEngine(); var cfg = Settings(); var s = Failed(e, cfg);
                e.BeginDispatch(s, cfg, Now.AddSeconds(30));
                var incident = s.IncidentId;
                s.FreshFailure = false; s.NextRetryUtc = null; s.LastEvidenceUtc = default(DateTime);
                var snapshot = Observe(EvidenceKind.Snapshot, "retry-offline", Now.AddHours(1));
                snapshot.IsFresh = false; snapshot.State = state; snapshot.IsTerminalFailure = state == SessionState.Failed;
                snapshot.CompletedAtUtc = Now.AddMinutes(1);
                e.Apply(s, snapshot, cfg, Now.AddHours(1));
                Check(s.State == state && !s.AwaitingConfirmation, "Verified saved " + state + " resolves uncertain delivery after restart");
                Check(!s.NextRetryUtc.HasValue && !s.FreshFailure && !e.CanDispatch(s, cfg, Now.AddDays(1)),
                    "Historical reconciliation never schedules another automatic message");
                if (state == SessionState.Completed)
                    Check(s.Events.Exists(x => x.Kind == "recovery") && s.IncidentId == null,
                        "A verified successfully completed post-dispatch turn confirms recovery after restart");
                if (state == SessionState.Failed)
                {
                    Check(s.IncidentId == incident && s.Attempts == 1 && s.FailedTurnId == "retry-offline",
                        "Historical post-dispatch failure preserves the accumulated incident budget");
                    snapshot.Id = "next-poll"; snapshot.TimestampUtc = Now.AddHours(1).AddSeconds(3); snapshot.State = SessionState.Unknown;
                    e.Apply(s, snapshot, cfg, Now.AddHours(1).AddSeconds(3));
                    Check(s.State == SessionState.Failed && !s.FreshFailure,
                        "Repeated confirmed terminal-failure snapshots retain attention without enabling retries");
                }
            }
            var engine = new RecoveryEngine(); var settings = Settings(); var pending = Failed(engine, settings);
            engine.BeginDispatch(pending, settings, Now.AddSeconds(30));
            var unrelated = Observe(EvidenceKind.Snapshot, "unrelated-old", Now.AddHours(1));
            unrelated.IsFresh = false; unrelated.State = SessionState.Completed; unrelated.CompletedAtUtc = Now.AddSeconds(10);
            engine.Apply(pending, unrelated, settings, Now.AddHours(1));
            Check(pending.AwaitingConfirmation && !pending.Events.Exists(x => x.Kind == "recovery"),
                "Metadata updated after dispatch cannot substitute for an actual post-dispatch completion timestamp");
        }
    }
}

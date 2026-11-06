using System;
using System.Collections.Generic;

namespace CodexAutoContinue.Core
{
    public sealed class RecoveryEngine
    {
        public void Apply(SessionRecord session, SessionObservation observation, AppSettings settings, DateTime now)
        {
            if (session == null || observation == null || settings == null) throw new ArgumentNullException();
            if (!string.IsNullOrEmpty(session.ThreadId) && session.ThreadId != observation.ThreadId)
                throw new ArgumentException("会话观察结果与目标会话不匹配。");
            var authoritative = observation.IsFresh && observation.Kind != EvidenceKind.Snapshot;
            if ((!string.IsNullOrEmpty(observation.Id) && session.LastObservationId == observation.Id) ||
                observation.TimestampUtc < session.LastEvidenceUtc) return;

            session.ThreadId = observation.ThreadId;
            if (!string.IsNullOrWhiteSpace(observation.Title)) session.Title = observation.Title;
            if (observation.Workspace != null) session.Workspace = observation.Workspace;
            session.LastObservationId = observation.Id;
            if (observation.TimestampUtc > session.LastActivityUtc) session.LastActivityUtc = observation.TimestampUtc;
            if (authoritative) session.LastEvidenceUtc = observation.TimestampUtc;

            if (!observation.IsFresh || observation.Kind == EvidenceKind.Snapshot)
            {
                ApplySnapshot(session, observation, now);
                return;
            }

            switch (observation.Kind)
            {
                case EvidenceKind.TerminalFailure:
                    ApplyFailure(session, observation, settings, now);
                    return;
                case EvidenceKind.Started:
                    if (!session.AwaitingConfirmation && session.IncidentId != null && observation.TurnId == session.FailedTurnId) return;
                    session.TurnId = observation.TurnId;
                    if (session.AwaitingConfirmation)
                    {
                        if (IsNewExecution(session, observation)) session.RecoveryTurnId = observation.TurnId;
                        session.State = SessionState.Pending;
                        session.Reason = "已观察到新执行轮次，等待实际执行进展确认。";
                    }
                    else
                    {
                        CancelSchedule(session);
                        session.State = SessionState.Running;
                        session.Reason = "已观察到新执行轮次。";
                    }
                    return;
                case EvidenceKind.Progress:
                    if (!session.AwaitingConfirmation && session.IncidentId != null && observation.TurnId == session.FailedTurnId) return;
                    if (session.AwaitingConfirmation)
                    {
                        if (!IsNewExecution(session, observation)) return;
                        ConfirmRecovery(session, now);
                    }
                    else CancelSchedule(session);
                    session.TurnId = observation.TurnId;
                    session.State = SessionState.Running;
                    session.Reason = "已观察到模型输出或工具执行进展。";
                    return;
                case EvidenceKind.Completed:
                    if (session.AwaitingConfirmation && IsNewExecution(session, observation)) ConfirmRecovery(session, now);
                    ClearIncident(session);
                    session.TurnId = observation.TurnId;
                    session.State = SessionState.Completed;
                    session.Reason = "当前执行轮次已正常完成。";
                    AddEvent(session, "state", session.Reason, now);
                    return;
                case EvidenceKind.Stopped:
                    ClearIncident(session);
                    session.TurnId = observation.TurnId;
                    session.State = SessionState.Stopped;
                    session.Reason = "用户已停止任务，等待人工操作。";
                    AddEvent(session, "state", session.Reason, now);
                    return;
                case EvidenceKind.Approval:
                    CancelSchedule(session);
                    session.AwaitingConfirmation = false;
                    session.TurnId = observation.TurnId;
                    session.State = SessionState.WaitingApproval;
                    session.Reason = "等待用户批准或输入，不自动续接。";
                    AddEvent(session, "state", session.Reason, now);
                    return;
                case EvidenceKind.InternalRetry:
                    CancelSchedule(session);
                    session.TurnId = observation.TurnId;
                    session.State = SessionState.InternalRetry;
                    session.Reason = "Codex 正在内部重试，保持观察。";
                    return;
                case EvidenceKind.UserMessage:
                    if (observation.IsContinuationMessage)
                    {
                        if (session.AwaitingConfirmation) session.Reason = "已观察到续接消息，仍需执行进展确认。";
                        return;
                    }
                    ClearIncident(session);
                    session.TurnId = observation.TurnId;
                    session.State = SessionState.Unknown;
                    session.Reason = "已收到新的用户任务，等待运行状态确认。";
                    AddEvent(session, "state", session.Reason, now);
                    return;
            }
        }

        public bool CanDispatch(SessionRecord session, AppSettings settings, DateTime now)
        {
            if (session == null || settings == null) return false;
            return settings.GlobalMonitoring && session.Monitoring && session.FreshFailure &&
                !session.AwaitingConfirmation && session.NextRetryUtc.HasValue && session.NextRetryUtc.Value <= now &&
                !string.IsNullOrEmpty(session.IncidentId) && !string.IsNullOrEmpty(session.FailedTurnId) &&
                session.Attempts < AttemptLimit(settings) &&
                (session.State == SessionState.NetworkFailure || session.State == SessionState.QuotaWait);
        }

        public void BeginDispatch(SessionRecord session, AppSettings settings, DateTime now)
        {
            if (session == null || settings == null) throw new ArgumentNullException();
            if (session.AwaitingConfirmation || !session.FreshFailure || string.IsNullOrEmpty(session.IncidentId) ||
                string.IsNullOrEmpty(session.FailedTurnId) || session.Attempts >= AttemptLimit(settings) ||
                (session.State != SessionState.NetworkFailure && session.State != SessionState.QuotaWait))
                throw new InvalidOperationException("当前会话不满足续接条件，或上一条消息仍待确认。");
            var message = settings.EffectiveMessage();
            session.Attempts++;
            session.DispatchMessage = message;
            session.DispatchUtc = now;
            session.RecoveryTurnId = null;
            session.NextRetryUtc = null;
            session.AwaitingConfirmation = true;
            session.State = SessionState.Pending;
            session.Reason = "已记录续接意图，等待消息发送及真实执行确认。";
            AddEvent(session, "dispatch", "已记录第 " + session.Attempts + " 次续接，等待恢复确认。", now);
        }

        public void MarkDelivery(SessionRecord session, string detail, DateTime now)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (!session.AwaitingConfirmation) return;
            session.State = SessionState.Pending;
            session.Reason = string.IsNullOrWhiteSpace(detail) ? "消息处理结果待确认，等待真实执行事件。" : detail;
            AddEvent(session, "delivery", session.Reason, now);
        }

        private static void ApplySnapshot(SessionRecord session, SessionObservation observation, DateTime now)
        {
            // A saved terminal turn can reconcile an uncertain send, but can never enable a new retry.
            if (session.AwaitingConfirmation)
            {
                if (!session.DispatchUtc.HasValue || !observation.CompletedAtUtc.HasValue ||
                    observation.CompletedAtUtc.Value < FloorSecond(session.DispatchUtc.Value) ||
                    observation.CompletedAtUtc.Value > now.AddMinutes(1) || string.IsNullOrEmpty(observation.TurnId) ||
                    observation.TurnId == session.FailedTurnId) return;
                if (observation.State == SessionState.Completed)
                {
                    ConfirmRecovery(session, now);
                    session.TurnId = observation.TurnId;
                    session.State = SessionState.Completed;
                    session.Reason = "已核对续接后的执行轮次正常完成。";
                }
                else if (observation.State == SessionState.Stopped)
                {
                    ClearIncident(session);
                    session.TurnId = observation.TurnId;
                    session.State = SessionState.Stopped;
                    session.Reason = "已核对续接后的执行轮次被用户停止。";
                    AddEvent(session, "state", session.Reason, now);
                }
                else if (observation.IsTerminalFailure)
                {
                    session.AwaitingConfirmation = false;
                    CancelSchedule(session);
                    session.TurnId = observation.TurnId;
                    session.FailedTurnId = observation.TurnId;
                    session.RecoveryTurnId = null;
                    session.State = SessionState.Failed;
                    session.Reason = "离线期间的续接轮次仍然失败，已保留次数记录，等待人工检查。";
                    AddEvent(session, "state", session.Reason, now);
                }
                return;
            }
            if (observation.TurnId == session.FailedTurnId && session.FreshFailure) return;
            if (observation.State == SessionState.Unknown && observation.TurnId == session.TurnId &&
                session.LastEvidenceUtc != default(DateTime) &&
                (session.State == SessionState.Running || session.State == SessionState.InternalRetry || session.State == SessionState.WaitingApproval)) return;
            session.TurnId = observation.TurnId;
            CancelSchedule(session);
            session.State = observation.Kind == EvidenceKind.TerminalFailure || observation.IsTerminalFailure ? SessionState.Failed : observation.State;
            if (observation.Kind == EvidenceKind.Completed) session.State = SessionState.Completed;
            if (observation.Kind == EvidenceKind.Stopped) session.State = SessionState.Stopped;
            session.Reason = string.IsNullOrWhiteSpace(observation.Reason) ? "本地历史状态，等待新的运行事件。" : observation.Reason;
        }

        private static void ApplyFailure(SessionRecord session, SessionObservation observation, AppSettings settings, DateTime now)
        {
            if (string.IsNullOrEmpty(observation.TurnId))
            {
                CancelSchedule(session);
                session.State = SessionState.Failed;
                session.Reason = "失败事件缺少执行轮次，需人工检查。";
                return;
            }
            if (session.AwaitingConfirmation && (!session.DispatchUtc.HasValue ||
                observation.TimestampUtc < FloorSecond(session.DispatchUtc.Value) || observation.TurnId == session.FailedTurnId)) return;
            if (!session.AwaitingConfirmation && session.FreshFailure && session.FailedTurnId == observation.TurnId) return;

            var newIncident = string.IsNullOrEmpty(session.IncidentId);
            if (newIncident)
            {
                session.IncidentId = Guid.NewGuid().ToString("N");
                session.Attempts = 0;
            }
            session.FailedTurnId = observation.TurnId;
            session.TurnId = observation.TurnId;
            session.RecoveryTurnId = null;
            session.AwaitingConfirmation = false;
            session.FreshFailure = true;
            session.NextRetryUtc = null;
            session.State = FailureClassifier.Classify(observation.FailureCode, observation.Reason, observation.RetryAtUtc);
            session.Reason = string.IsNullOrWhiteSpace(observation.Reason) ? "执行失败，需检查异常原因。" : observation.Reason;
            if (newIncident) AddEvent(session, "failure", session.Reason, now);

            if (session.State != SessionState.NetworkFailure && session.State != SessionState.QuotaWait) return;
            if (session.Attempts >= AttemptLimit(settings))
            {
                session.State = SessionState.Failed;
                session.Reason = "已达到本次异常的续接次数上限，请人工检查。";
                AddEvent(session, "exhausted", session.Reason, now);
                return;
            }
            var baseDelay = Math.Max(1, Math.Min(300, settings.NetworkDelaySeconds));
            var delay = Math.Min(300, baseDelay * Math.Pow(2, Math.Min(session.Attempts, 10)));
            session.NextRetryUtc = observation.RetryAtUtc.HasValue ?
                (observation.RetryAtUtc.Value > now ? observation.RetryAtUtc.Value : now) : now.AddSeconds(delay);
        }

        private static bool IsNewExecution(SessionRecord session, SessionObservation observation)
        {
            return observation.IsFresh && session.DispatchUtc.HasValue && observation.TimestampUtc >= FloorSecond(session.DispatchUtc.Value) &&
                !string.IsNullOrEmpty(observation.TurnId) && observation.TurnId != session.FailedTurnId;
        }

        private static DateTime FloorSecond(DateTime value) { return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond)); }

        private static void ConfirmRecovery(SessionRecord session, DateTime now)
        {
            AddEvent(session, "recovery", "已观察到新的真实执行进展，确认任务恢复。", now);
            ClearIncident(session);
        }
        private static void CancelSchedule(SessionRecord session)
        {
            session.FreshFailure = false;
            session.NextRetryUtc = null;
        }
        private static void ClearIncident(SessionRecord session)
        {
            CancelSchedule(session);
            session.IncidentId = null;
            session.FailedTurnId = null;
            session.RecoveryTurnId = null;
            session.Attempts = 0;
            session.AwaitingConfirmation = false;
            session.DispatchUtc = null;
            session.DispatchMessage = null;
        }
        private static int AttemptLimit(AppSettings settings) { return Math.Max(1, Math.Min(20, settings.MaxAttempts)); }
        private static void AddEvent(SessionRecord session, string kind, string message, DateTime now)
        {
            if (session.Events == null) session.Events = new List<MonitorEvent>();
            session.Events.Insert(0, new MonitorEvent { ThreadId = session.ThreadId, TimeUtc = now, Kind = kind, Message = message });
            if (session.Events.Count > 100) session.Events.RemoveRange(100, session.Events.Count - 100);
        }
    }
}

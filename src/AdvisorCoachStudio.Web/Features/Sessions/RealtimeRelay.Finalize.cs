using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

namespace AdvisorCoachStudio.Web.Features.Sessions;

/// <summary>Idempotent end and finalization path shared by user End, duration cap, disconnects, and errors.</summary>
public sealed partial class RealtimeRelay
{
    private bool _awaitingCommit;

    private void RequestEnd(SessionEndReason reason, string? errorCode, string? message, bool retryable)
    {
        if (_finished)
        {
            return;
        }

        if (errorCode is not null && message is not null)
        {
            SendError(errorCode, message, retryable);
        }

        if (_endReason == SessionEndReason.None)
        {
            _endReason = reason;
        }

        BeginFinalization();
    }

    private void BeginFinalization()
    {
        if (_finalizing)
        {
            return;
        }

        _finalizing = true;
        var now = Now;
        _finalizeDeadline = now + TimeSpan.FromSeconds(Math.Clamp(_deps.Options.FinalizationDrainSeconds, 0, 10));
        if (_mode == RelayMode.Roleplay)
        {
            _session.TryTransition(ConversationState.Ending, now.UtcDateTime, _endReason);
        }

        SendState("ending", critical: true);

        // Stop output immediately and never let another reply play. Queued (unsent) audio is now useless.
        _browserOut.DropQueuedAudio();
        foreach (var item in _outputItems.Where(i => !i.Cleared && !i.PlaybackDone))
        {
            var unheard = item.HasUnheardAudio;
            item.Cleared = true;
            item.Pending.Clear();
            item.PendingBytes = 0;
            _browserOut.TryEnqueue(AppMessages.PlaybackClear(item.ResponseId, item.ItemId), critical: true);
            if (unheard)
            {
                Forward(_assembler.MarkNotFullyHeard(item.ItemId, item.ContentIndex));
            }
        }

        foreach (var response in _responses.Values.Where(r => !r.Done))
        {
            // Text of an unfinished response may never have been played, even without audio-backed items.
            Forward(_assembler.MarkResponseNotFullyHeard(response.Id));
            if (!response.Cleared)
            {
                response.Cleared = true;
                EnqueueProvider(ProviderClientEvents.ResponseCancel(response.Id));
            }
        }

        // Truncations still waiting for playback.stopped are sent now with the last reported position.
        foreach (var pending in _pendingTruncations.ToList())
        {
            _pendingTruncations.Remove(pending);
            SendTruncate(pending.Item);
        }

        // Commit in-progress advisor speech (VAD saw speech that was not yet committed) so it is transcribed.
        // A manual commit never creates a response, and a race with the VAD's own commit only yields a benign error.
        if (_mode == RelayMode.Roleplay && _speechItemId is not null && !_providerClosed)
        {
            _awaitingCommit = EnqueueProvider(ProviderClientEvents.CommitAudio());
        }

        if (_mode == RelayMode.Roleplay)
        {
            _session.TryTransition(ConversationState.TranscriptFinalizing, Now.UtcDateTime);
        }
    }

    private bool ReadyToFreeze() =>
        _providerClosed || Now >= _finalizeDeadline || (!_awaitingCommit && !_assembler.HasPendingWork);

    private void CompleteFinalization()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (!_finalizing)
        {
            BeginFinalization();
        }

        var now = Now;
        var frozen = _assembler.Freeze();
        var complete = frozen.Complete && !_awaitingCommit && _endReason != SessionEndReason.ProviderError;
        var duration = _active ? (now - _startedAt).TotalSeconds : 0;
        RelayMetrics.SessionSeconds.Record(duration, new KeyValuePair<string, object?>("mode", _mode.ToString()));
        if (!complete)
        {
            RelayMetrics.TranscriptsIncomplete.Add(1);
        }

        if (_mode == RelayMode.Roleplay)
        {
            FinishRoleplay(frozen, complete, now);
        }
        else
        {
            FinishDemonstration(frozen, complete, now);
        }

        _logger.LogInformation(
            "Realtime {Mode} {SessionId} ended: reason={Reason} duration={Duration:F1}s turns={Turns} complete={Complete} interruptions={Interruptions} lateEvents={Late} duplicateEvents={Duplicates} lateAudio={LateAudio} mutedDrops={MutedDrops} ignoredProgress={IgnoredProgress} browserClosed={BrowserClosed} correlation={CorrelationId}",
            _mode, _session.Id, _endReason, duration, frozen.Turns.Count, complete, _interruptions, _assembler.LateEventCount,
            _assembler.DuplicateEventCount, _lateAudioDeltas, _droppedWhileMuted, _ignoredProgress, _browserClosed, _correlationId);
    }

    private void FinishRoleplay(FrozenTranscript frozen, bool complete, DateTimeOffset now)
    {
        _session.FreezeTranscript(frozen.Turns, complete);
        _session.TryTransition(ConversationState.Ended, now.UtcDateTime, _endReason);
        if (_session.IsDiscarded)
        {
            return;
        }

        // Queue when configured, even if the service is temporarily degraded; the worker retries and reports failures.
        if (_deps.Readiness.Reports().Status != ProviderReadinessStatus.SetupRequired)
        {
            if (_session.TryQueueReport())
            {
                _deps.Reports.Enqueue(_session.Id);
            }
        }
        else
        {
            _session.SetReportSetupRequired();
        }

        var snapshot = _session.Snapshot();
        _browserOut.TryEnqueue(AppMessages.Ended(ReasonName(_endReason), complete ? "complete" : "incomplete", ReportStatusName(snapshot.ReportStatus), $"/sessions/{_session.Id}/report"), critical: true);
        SendState("ended", critical: true);
    }

    private void FinishDemonstration(FrozenTranscript frozen, bool complete, DateTimeOffset now)
    {
        var text = string.Join(Environment.NewLine, frozen.Turns.Where(t => t.Speaker == TurnSpeaker.Client && t.Text.Length > 0).Select(t => t.Text));
        var status = _endReason switch
        {
            SessionEndReason.Completed => DemonstrationStatus.Completed,
            SessionEndReason.UserEnded or SessionEndReason.DurationLimit or SessionEndReason.Disconnected => DemonstrationStatus.Stopped,
            _ => DemonstrationStatus.Failed,
        };
        _demonstration!.Finish(status, text, complete && status == DemonstrationStatus.Completed, now.UtcDateTime, status == DemonstrationStatus.Failed ? "provider_error" : null);
        _browserOut.TryEnqueue(AppMessages.Ended(ReasonName(_endReason), complete ? "complete" : "incomplete", "not_applicable", null), critical: true);
        SendState("ended", critical: true);
    }

    public static string ReasonName(SessionEndReason reason) => reason switch
    {
        SessionEndReason.UserEnded => "user_ended",
        SessionEndReason.DurationLimit => "duration_limit",
        SessionEndReason.Disconnected => "disconnected",
        SessionEndReason.ProviderError => "provider_error",
        SessionEndReason.ConnectionQuality => "connection_quality",
        SessionEndReason.AccessExpired => "access_expired",
        SessionEndReason.Completed => "completed",
        _ => "none",
    };

    public static string ReportStatusName(ReportStatus status) => status switch
    {
        ReportStatus.NotRequested => "not_requested",
        ReportStatus.SetupRequired => "setup_required",
        ReportStatus.Queued => "queued",
        ReportStatus.Generating => "generating",
        ReportStatus.Ready => "ready",
        ReportStatus.Failed => "failed",
        _ => "unknown",
    };
}

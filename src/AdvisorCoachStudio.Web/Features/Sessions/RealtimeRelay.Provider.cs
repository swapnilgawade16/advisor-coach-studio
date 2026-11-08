using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

namespace AdvisorCoachStudio.Web.Features.Sessions;

public sealed partial class RealtimeRelay
{
    /// <summary>Server-built demonstration prompt (demonstration mode only).</summary>
    internal string? DemonstrationInstructions { get; init; }

    private static readonly HashSet<string> BenignErrorCodes = new(StringComparer.Ordinal)
    {
        "response_cancel_not_active",
        "input_audio_buffer_commit_empty",
        "conversation_already_has_active_response",
        "item_truncate_invalid_item_id",
    };

    private static readonly HashSet<string> FatalErrorTypes = new(StringComparer.Ordinal)
    {
        "server_error",
        "authentication_error",
        "permission_error",
        "rate_limit_error",
        "insufficient_quota",
    };

    private int _unknownEvents;

    private void HandleProvider(ProviderEvent evt)
    {
        switch (evt.Type)
        {
            case "error":
                HandleProviderError(evt);
                break;

            case "input_audio_buffer.speech_started":
                if (_mode == RelayMode.Roleplay)
                {
                    _speechItemId = evt.ItemId;
                    _assembler.OnSpeechStarted(evt.ItemId!);
                    HandleInterruption();
                }

                break;

            case "input_audio_buffer.speech_stopped":
                _assembler.OnSpeechStopped(evt.ItemId!);
                break;

            case "input_audio_buffer.committed":
                if (_speechItemId == evt.ItemId)
                {
                    _speechItemId = null;
                }

                _awaitingCommit = false;
                if (_mode == RelayMode.Roleplay)
                {
                    _assembler.OnInputCommitted(evt.ItemId!, evt.PreviousItemId);
                }

                break;

            case "conversation.item.added":
                if (evt.ItemType is null or "message")
                {
                    _assembler.OnItemAdded(evt.ItemId!, evt.PreviousItemId, _mode == RelayMode.Roleplay ? evt.ItemRole : null);
                }

                break;

            case "conversation.item.input_audio_transcription.delta":
                Forward(_assembler.OnInputTranscriptionDelta(evt.ItemId!, evt.ContentIndex, evt.Delta!));
                break;

            case "conversation.item.input_audio_transcription.completed":
                Forward(_assembler.OnInputTranscriptionCompleted(evt.ItemId!, evt.ContentIndex, evt.Transcript!, evt.EventId));
                break;

            case "conversation.item.input_audio_transcription.failed":
                _logger.LogWarning("Input transcription failed in session {SessionId}: {ErrorType}/{ErrorCode}.", _session.Id, evt.ErrorType, evt.ErrorCode);
                Forward(_assembler.OnInputTranscriptionFailed(evt.ItemId!, evt.ContentIndex, evt.EventId));
                break;

            case "response.created":
                OnResponseCreated(evt.ResponseId!);
                break;

            case "response.output_item.added":
                if (!IsResponseCleared(evt.ResponseId!))
                {
                    _assembler.OnOutputItemAdded(evt.ResponseId!, evt.ItemId!);
                }

                break;

            case "response.output_audio.delta":
                OnAudioDelta(evt);
                break;

            case "response.output_audio.done":
                OnAudioDone(evt.ResponseId!, evt.ItemId!, evt.ContentIndex);
                break;

            case "response.output_audio_transcript.delta":
                if (!IsResponseCleared(evt.ResponseId!) || _assembler.Snapshot(evt.ItemId!, evt.ContentIndex) is not null)
                {
                    Forward(_assembler.OnOutputTranscriptDelta(evt.ResponseId!, evt.ItemId!, evt.ContentIndex, evt.Delta!));
                }

                break;

            case "response.output_audio_transcript.done":
                if (!IsResponseCleared(evt.ResponseId!) || _assembler.Snapshot(evt.ItemId!, evt.ContentIndex) is not null)
                {
                    Forward(_assembler.OnOutputTranscriptDone(evt.ResponseId!, evt.ItemId!, evt.ContentIndex, evt.Transcript!, evt.EventId));
                }

                break;

            case "response.done":
                OnResponseDone(evt);
                break;

            case "conversation.item.truncated":
                _logger.LogDebug("Provider acknowledged truncation at {AudioEndMs} ms.", evt.AudioEndMs);
                break;

            case "session.created":
            case "session.updated":
            case "response.output_item.done":
            case "conversation.item.done":
                break;

            default:
                if (!evt.IsKnown && _unknownEvents++ < 20)
                {
                    _logger.LogDebug("Ignoring unknown provider event type {Type}.", evt.Type);
                }

                break;
        }
    }

    private void HandleProviderError(ProviderEvent evt)
    {
        RelayMetrics.ProviderErrors.Add(1, new KeyValuePair<string, object?>("code", evt.ErrorCode ?? evt.ErrorType));
        if (evt.ErrorCode is { } code && BenignErrorCodes.Contains(code))
        {
            if (code == "input_audio_buffer_commit_empty")
            {
                _awaitingCommit = false;
            }

            _logger.LogDebug("Benign provider error {ErrorCode} in session {SessionId}.", code, _session.Id);
            return;
        }

        _logger.LogWarning("Provider error in session {SessionId}: {ErrorType}/{ErrorCode}.", _session.Id, evt.ErrorType, evt.ErrorCode);
        if (FatalErrorTypes.Contains(evt.ErrorType ?? "") || evt.ErrorCode is "rate_limit_exceeded" or "session_expired")
        {
            if (evt.ErrorType is "authentication_error" or "permission_error")
            {
                _deps.Health.RealtimeFailed(true);
            }

            RequestEnd(SessionEndReason.ProviderError, "provider_error", "The voice service reported an error. Your completed turns were kept; please start a new attempt.", true);
        }
    }

    private void Forward(TurnUpdate? update)
    {
        if (update is null)
        {
            return;
        }

        if (_mode == RelayMode.Roleplay && _assembler.Snapshot(update.ItemId, update.ContentIndex) is { } live)
        {
            _session.RecordLiveTurn(live);
        }

        var speaker = Speaker(update.Speaker);
        if (update.IsFinal)
        {
            _browserOut.TryEnqueue(AppMessages.TranscriptFinal(update.TurnId, update.Sequence == int.MaxValue ? 0 : update.Sequence, speaker, update.Text, StatusName(update.Status), update.FullyHeard));
        }
        else
        {
            _browserOut.TryEnqueue(AppMessages.TranscriptPartial(update.TurnId, speaker, update.Text));
        }
    }

    private void Forward(IReadOnlyList<TurnUpdate> updates)
    {
        foreach (var update in updates)
        {
            Forward(update);
        }
    }

    private static string StatusName(TurnStatus status) => status switch
    {
        TurnStatus.Final => "final",
        TurnStatus.Failed => "failed",
        TurnStatus.Interrupted => "interrupted",
        _ => "pending",
    };
}

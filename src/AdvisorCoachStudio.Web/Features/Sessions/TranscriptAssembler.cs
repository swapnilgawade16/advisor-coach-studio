using System.Text;
using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.Web.Features.Sessions;

/// <summary>A transcript change to forward to the browser.</summary>
public sealed record TurnUpdate(Guid TurnId, string ItemId, int ContentIndex, TurnSpeaker Speaker, string Text, bool IsFinal, TurnStatus Status, bool? FullyHeard, int Sequence);

public sealed record FrozenTranscript(IReadOnlyList<TranscriptTurn> Turns, bool Complete);

/// <summary>
/// Server-authoritative transcript assembly (spec section 10). Correlates provider events by item/content IDs,
/// orders turns by provider conversation order (not arrival or completion time), deduplicates final events,
/// and preserves failure/interruption markers. Not thread-safe: the relay calls it from a single event loop.
/// </summary>
public sealed class TranscriptAssembler(Guid sessionId, TimeProvider time)
{
    private readonly List<string> _order = [];
    private readonly HashSet<string> _placed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _placedByFallback = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ItemId, int ContentIndex), Turn> _turns = [];
    private readonly HashSet<string> _completedResponses = new(StringComparer.Ordinal);
    private bool _frozen;

    public int LateEventCount { get; private set; }
    public int DuplicateEventCount { get; private set; }
    public bool IsFrozen => _frozen;

    private sealed class Turn
    {
        public required Guid Id { get; init; }
        public required string ItemId { get; init; }
        public required int ContentIndex { get; init; }
        public required TurnSpeaker Speaker { get; init; }
        public required TurnSource Source { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public string? ResponseId { get; set; }
        public StringBuilder Provisional { get; } = new();
        public string? FinalText { get; set; }
        public TurnStatus Status { get; set; } = TurnStatus.Pending;
        public bool Interrupted { get; set; }
        public bool PlaybackComplete { get; set; }
        public bool HadAudio { get; set; }
        public bool Committed { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? EndedAtUtc { get; set; }
        public string? SourceEventId { get; set; }
    }

    // ---- Ordering -------------------------------------------------------------------------

    public void OnItemAdded(string itemId, string? previousItemId, string? role)
    {
        if (_frozen)
        {
            LateEventCount++;
            return;
        }

        Place(itemId, previousItemId, authoritative: true);
        if (role == "user")
        {
            var turn = EnsureTurn(itemId, 0, TurnSpeaker.Advisor, TurnSource.InputTranscription);
            turn.Committed = true;
        }
    }

    public void OnInputCommitted(string itemId, string? previousItemId)
    {
        if (_frozen)
        {
            LateEventCount++;
            return;
        }

        Place(itemId, previousItemId, authoritative: true);
        EnsureTurn(itemId, 0, TurnSpeaker.Advisor, TurnSource.InputTranscription).Committed = true;
    }

    public void OnSpeechStarted(string itemId)
    {
        if (_frozen)
        {
            return;
        }

        var turn = EnsureTurn(itemId, 0, TurnSpeaker.Advisor, TurnSource.InputTranscription);
        turn.StartedAtUtc ??= Now;
    }

    public void OnSpeechStopped(string itemId)
    {
        if (!_frozen && _turns.TryGetValue((itemId, 0), out var turn))
        {
            turn.EndedAtUtc = Now;
        }
    }

    // ---- Advisor (input transcription) ----------------------------------------------------

    public TurnUpdate? OnInputTranscriptionDelta(string itemId, int contentIndex, string delta)
    {
        if (_frozen)
        {
            LateEventCount++;
            return null;
        }

        var turn = EnsureTurn(itemId, contentIndex, TurnSpeaker.Advisor, TurnSource.InputTranscription);
        if (turn.FinalText is not null || turn.Status == TurnStatus.Failed)
        {
            DuplicateEventCount++;
            return null;
        }

        turn.Provisional.Append(delta);
        return ToUpdate(turn, isFinal: false);
    }

    public TurnUpdate? OnInputTranscriptionCompleted(string itemId, int contentIndex, string transcript, string? eventId)
    {
        if (_frozen)
        {
            LateEventCount++;
            return null;
        }

        var turn = EnsureTurn(itemId, contentIndex, TurnSpeaker.Advisor, TurnSource.InputTranscription);
        if (turn.FinalText is not null)
        {
            DuplicateEventCount++;
            return null;
        }

        turn.FinalText = transcript.Trim();
        turn.Status = TurnStatus.Final;
        turn.SourceEventId = eventId;
        turn.Committed = true;
        return ToUpdate(turn, isFinal: true);
    }

    public TurnUpdate? OnInputTranscriptionFailed(string itemId, int contentIndex, string? eventId)
    {
        if (_frozen)
        {
            LateEventCount++;
            return null;
        }

        var turn = EnsureTurn(itemId, contentIndex, TurnSpeaker.Advisor, TurnSource.InputTranscription);
        if (turn.FinalText is not null || turn.Status == TurnStatus.Failed)
        {
            DuplicateEventCount++;
            return null;
        }

        turn.Status = TurnStatus.Failed;
        turn.SourceEventId = eventId;
        turn.Committed = true;
        return ToUpdate(turn, isFinal: true);
    }

    // ---- Client (output audio transcript) -------------------------------------------------

    public void OnOutputItemAdded(string responseId, string itemId)
    {
        if (_frozen)
        {
            return;
        }

        Place(itemId, null, authoritative: false);
    }

    public void OnOutputAudio(string responseId, string itemId, int contentIndex)
    {
        if (_frozen)
        {
            return;
        }

        Place(itemId, null, authoritative: false);
        var turn = EnsureTurn(itemId, contentIndex, TurnSpeaker.Client, TurnSource.OutputAudioTranscript);
        turn.ResponseId ??= responseId;
        turn.HadAudio = true;
        turn.StartedAtUtc ??= Now;
    }

    public TurnUpdate? OnOutputTranscriptDelta(string responseId, string itemId, int contentIndex, string delta)
    {
        if (_frozen)
        {
            LateEventCount++;
            return null;
        }

        Place(itemId, null, authoritative: false);
        var turn = EnsureTurn(itemId, contentIndex, TurnSpeaker.Client, TurnSource.OutputAudioTranscript);
        turn.ResponseId ??= responseId;
        if (turn.FinalText is not null)
        {
            DuplicateEventCount++;
            return null;
        }

        turn.Provisional.Append(delta);
        return ToUpdate(turn, isFinal: false);
    }

    public TurnUpdate? OnOutputTranscriptDone(string responseId, string itemId, int contentIndex, string transcript, string? eventId)
    {
        if (_frozen)
        {
            LateEventCount++;
            return null;
        }

        Place(itemId, null, authoritative: false);
        var turn = EnsureTurn(itemId, contentIndex, TurnSpeaker.Client, TurnSource.OutputAudioTranscript);
        turn.ResponseId ??= responseId;
        if (turn.FinalText is not null)
        {
            DuplicateEventCount++;
            return null;
        }

        turn.FinalText = transcript.Trim();
        turn.SourceEventId = eventId;
        turn.EndedAtUtc ??= Now;
        turn.Status = turn.Interrupted ? TurnStatus.Interrupted : TurnStatus.Final;
        return ToUpdate(turn, isFinal: true);
    }

    /// <summary>
    /// Applies response.done. Its transcripts are only a fallback when no transcript.done arrived; they are never
    /// concatenated with an existing final transcript.
    /// </summary>
    public IReadOnlyList<TurnUpdate> OnResponseDone(string responseId, string status, string? reason, IReadOnlyList<(string ItemId, int ContentIndex, string? Transcript)> outputs)
    {
        if (_frozen)
        {
            LateEventCount++;
            return [];
        }

        _completedResponses.Add(responseId);
        var updates = new List<TurnUpdate>();
        var interrupted = status == "cancelled" && reason == "turn_detected";
        foreach (var (itemId, contentIndex, transcript) in outputs)
        {
            _turns.TryGetValue((itemId, contentIndex), out var turn);
            if (turn is null)
            {
                if (string.IsNullOrWhiteSpace(transcript))
                {
                    continue;
                }

                Place(itemId, null, authoritative: false);
                turn = EnsureTurn(itemId, contentIndex, TurnSpeaker.Client, TurnSource.OutputAudioTranscript);
            }

            turn.ResponseId ??= responseId;
            if (interrupted)
            {
                turn.Interrupted = true;
            }

            if (turn.FinalText is null)
            {
                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    turn.FinalText = transcript.Trim();
                    turn.Status = turn.Interrupted ? TurnStatus.Interrupted : TurnStatus.Final;
                }
                else
                {
                    turn.Status = turn.Interrupted ? TurnStatus.Interrupted : TurnStatus.Failed;
                }

                updates.Add(ToUpdate(turn, isFinal: true));
            }
            else if (turn.Interrupted && turn.Status != TurnStatus.Interrupted)
            {
                turn.Status = TurnStatus.Interrupted;
                updates.Add(ToUpdate(turn, isFinal: true));
            }
        }

        // Turns of this response that never received any transcript are finalized as failed captions.
        foreach (var turn in _turns.Values.Where(t => t.ResponseId == responseId && t.FinalText is null && t.Status == TurnStatus.Pending))
        {
            turn.Status = turn.Interrupted ? TurnStatus.Interrupted : TurnStatus.Failed;
            updates.Add(ToUpdate(turn, isFinal: true));
        }

        // A turn_detected cancellation applies to every turn of the response that was not fully played,
        // not only the turns listed in response.done output.
        if (interrupted)
        {
            updates.AddRange(MarkResponseNotFullyHeard(responseId));
        }

        return updates;
    }

    /// <summary>Marks a client turn as not fully heard (interruption, or the session ended during playback).</summary>
    public TurnUpdate? MarkNotFullyHeard(string itemId, int contentIndex)
    {
        if (_frozen || !_turns.TryGetValue((itemId, contentIndex), out var turn) || turn.Speaker != TurnSpeaker.Client || turn.Interrupted)
        {
            return null;
        }

        turn.Interrupted = true;
        if (turn.FinalText is not null)
        {
            turn.Status = TurnStatus.Interrupted;
        }

        return ToUpdate(turn, isFinal: turn.FinalText is not null);
    }

    /// <summary>
    /// Marks every client turn of a response whose playback did not complete as not fully heard, including
    /// transcript-only turns that never had audio played.
    /// </summary>
    public IReadOnlyList<TurnUpdate> MarkResponseNotFullyHeard(string responseId)
    {
        if (_frozen)
        {
            return [];
        }

        var updates = new List<TurnUpdate>();
        foreach (var turn in _turns.Values.Where(t => t.Speaker == TurnSpeaker.Client && t.ResponseId == responseId && !t.PlaybackComplete).ToList())
        {
            if (MarkNotFullyHeard(turn.ItemId, turn.ContentIndex) is { } update)
            {
                updates.Add(update);
            }
        }

        return updates;
    }

    public void MarkPlaybackComplete(string itemId, int contentIndex)
    {
        if (!_frozen && _turns.TryGetValue((itemId, contentIndex), out var turn) && turn.Speaker == TurnSpeaker.Client)
        {
            turn.PlaybackComplete = true;
        }
    }

    /// <summary>True while committed advisor speech awaits transcription or a client response is unfinished.</summary>
    public bool HasPendingWork => _turns.Values.Any(t =>
        t.FinalText is null && t.Status == TurnStatus.Pending &&
        ((t.Speaker == TurnSpeaker.Advisor && t.Committed) ||
         (t.Speaker == TurnSpeaker.Client && t.ResponseId is not null && !_completedResponses.Contains(t.ResponseId))));

    public int FinalAdvisorTurnCount => _turns.Values.Count(t => t.Speaker == TurnSpeaker.Advisor && t.Status == TurnStatus.Final && !string.IsNullOrWhiteSpace(t.FinalText));

    /// <summary>Freezes the transcript and assigns canonical sequence numbers. Later events are counted as late and ignored.</summary>
    public FrozenTranscript Freeze()
    {
        _frozen = true;
        var complete = true;
        var result = new List<TranscriptTurn>();
        var ordered = _turns.Values
            .OrderBy(t => OrderIndex(t.ItemId))
            .ThenBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.ContentIndex);
        foreach (var turn in ordered)
        {
            var text = turn.FinalText ?? turn.Provisional.ToString().Trim();
            var status = turn.FinalText is not null || turn.Status != TurnStatus.Pending ? turn.Status : TurnStatus.Pending;
            if (turn.Interrupted && turn.Speaker == TurnSpeaker.Client)
            {
                status = TurnStatus.Interrupted;
            }

            if (status == TurnStatus.Final && text.Length == 0)
            {
                continue; // Nothing was recognized; there is no turn to show.
            }

            if (turn.Speaker == TurnSpeaker.Advisor && status == TurnStatus.Pending && !turn.Committed && text.Length == 0)
            {
                continue; // Speech detection only; no committed input exists.
            }

            if (turn.Speaker == TurnSpeaker.Client && text.Length == 0 && !turn.HadAudio)
            {
                continue;
            }

            if (status is TurnStatus.Pending or TurnStatus.Failed)
            {
                complete = false;
            }

            bool? fullyHeard = turn.Speaker == TurnSpeaker.Client
                ? (turn.Interrupted ? false : turn.PlaybackComplete ? true : null)
                : null;

            result.Add(new TranscriptTurn
            {
                Id = turn.Id,
                SessionId = sessionId,
                ProviderItemId = turn.ItemId,
                ProviderResponseId = turn.ResponseId,
                ContentIndex = turn.ContentIndex,
                Sequence = result.Count + 1,
                Speaker = turn.Speaker,
                Text = text,
                Status = status,
                Source = turn.Source,
                StartedAtUtc = turn.StartedAtUtc,
                EndedAtUtc = turn.EndedAtUtc,
                FullyHeard = fullyHeard,
                SourceEventId = turn.SourceEventId,
                CreatedAtUtc = turn.CreatedAtUtc,
            });
        }

        return new FrozenTranscript(result, complete);
    }

    /// <summary>Builds the live (not yet frozen) view of a turn for in-memory recording.</summary>
    public TranscriptTurn? Snapshot(string itemId, int contentIndex)
    {
        if (!_turns.TryGetValue((itemId, contentIndex), out var turn))
        {
            return null;
        }

        return new TranscriptTurn
        {
            Id = turn.Id,
            SessionId = sessionId,
            ProviderItemId = turn.ItemId,
            ProviderResponseId = turn.ResponseId,
            ContentIndex = turn.ContentIndex,
            Sequence = OrderIndex(turn.ItemId) == int.MaxValue ? int.MaxValue : OrderIndex(turn.ItemId) + 1,
            Speaker = turn.Speaker,
            Text = turn.FinalText ?? turn.Provisional.ToString().Trim(),
            Status = turn.Interrupted && turn.Speaker == TurnSpeaker.Client ? TurnStatus.Interrupted : turn.Status,
            Source = turn.Source,
            StartedAtUtc = turn.StartedAtUtc,
            EndedAtUtc = turn.EndedAtUtc,
            FullyHeard = turn.Speaker == TurnSpeaker.Client ? (turn.Interrupted ? false : turn.PlaybackComplete ? true : null) : null,
            SourceEventId = turn.SourceEventId,
            CreatedAtUtc = turn.CreatedAtUtc,
        };
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private int OrderIndex(string itemId)
    {
        var index = _order.IndexOf(itemId);
        return index < 0 ? int.MaxValue : index;
    }

    private void Place(string itemId, string? previousItemId, bool authoritative)
    {
        if (_placed.Contains(itemId))
        {
            if (!authoritative || !_placedByFallback.Remove(itemId) || previousItemId is null || !_placed.Contains(previousItemId))
            {
                return;
            }

            _order.Remove(itemId);
            _order.Insert(_order.IndexOf(previousItemId) + 1, itemId);
            return;
        }

        _placed.Add(itemId);
        if (!authoritative)
        {
            _placedByFallback.Add(itemId);
        }

        if (previousItemId is not null && _placed.Contains(previousItemId) && previousItemId != itemId)
        {
            _order.Insert(_order.IndexOf(previousItemId) + 1, itemId);
        }
        else
        {
            _order.Add(itemId);
        }
    }

    private Turn EnsureTurn(string itemId, int contentIndex, TurnSpeaker speaker, TurnSource source)
    {
        if (!_turns.TryGetValue((itemId, contentIndex), out var turn))
        {
            turn = new Turn
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                ContentIndex = contentIndex,
                Speaker = speaker,
                Source = source,
                CreatedAtUtc = Now,
            };
            _turns[(itemId, contentIndex)] = turn;
        }

        return turn;
    }

    private TurnUpdate ToUpdate(Turn turn, bool isFinal)
    {
        var text = turn.FinalText ?? turn.Provisional.ToString();
        var status = turn.Interrupted && turn.Speaker == TurnSpeaker.Client && turn.FinalText is not null ? TurnStatus.Interrupted : turn.Status;
        bool? fullyHeard = turn.Speaker == TurnSpeaker.Client ? (turn.Interrupted ? false : turn.PlaybackComplete ? true : null) : null;
        var index = OrderIndex(turn.ItemId);
        return new TurnUpdate(turn.Id, turn.ItemId, turn.ContentIndex, turn.Speaker, text, isFinal, status, fullyHeard, index == int.MaxValue ? int.MaxValue : index + 1);
    }
}

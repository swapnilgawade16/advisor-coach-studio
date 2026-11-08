namespace AdvisorCoachStudio.Web.Domain;

/// <summary>A normalized, server-authoritative transcript turn (spec section 10.1). Held in memory only.</summary>
public sealed record TranscriptTurn
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required string ProviderItemId { get; init; }
    public string? ProviderResponseId { get; init; }
    public int ContentIndex { get; init; }
    public int Sequence { get; init; }
    public required TurnSpeaker Speaker { get; init; }
    public required string Text { get; init; }
    public required TurnStatus Status { get; init; }
    public required TurnSource Source { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? EndedAtUtc { get; init; }
    public bool? FullyHeard { get; init; }
    public string? SourceEventId { get; init; }
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// True when the turn may be quoted as report evidence: a final, non-empty turn, and for client turns only when
    /// playback was confirmed complete (text the advisor never heard must not become evidence).
    /// </summary>
    public bool IsQuotable =>
        Status == TurnStatus.Final && !string.IsNullOrWhiteSpace(Text) &&
        (Speaker == TurnSpeaker.Advisor || FullyHeard == true);
}

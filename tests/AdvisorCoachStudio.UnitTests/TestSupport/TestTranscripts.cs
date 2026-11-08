using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>Synthetic canonical transcript turns for report and session tests.</summary>
internal static class TestTranscripts
{
    public static readonly DateTime BaseTimeUtc = new(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);

    public const string T1Client = "I checked my investments this morning and I'm really worried.";
    public const string T2Advisor = "I can hear that this has been a stressful morning for you.";
    public const string T3Client = "It has. My partner thinks we should sell everything.";
    public const string T4Advisor = "Before we decide anything, can you tell me when you are hoping to retire?";
    public const string T5ClientInterrupted = "In about five years, but I was going to say that";
    public const string T6Advisor = "Let's book a review next week to look at your plan together.";

    public static TranscriptTurn Turn(
        Guid sessionId,
        int sequence,
        TurnSpeaker speaker,
        string text,
        TurnStatus status = TurnStatus.Final,
        bool? fullyHeard = null) => new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ProviderItemId = $"item_synthetic_{sequence}",
            ProviderResponseId = speaker == TurnSpeaker.Client ? $"resp_synthetic_{sequence}" : null,
            ContentIndex = 0,
            Sequence = sequence,
            Speaker = speaker,
            Text = text,
            Status = status,
            Source = speaker == TurnSpeaker.Advisor ? TurnSource.InputTranscription : TurnSource.OutputAudioTranscript,
            FullyHeard = speaker == TurnSpeaker.Client ? (fullyHeard ?? true) : null,
            CreatedAtUtc = BaseTimeUtc.AddSeconds(sequence),
        };

    /// <summary>Six turns: three usable advisor turns (T2, T4, T6) and one interrupted client turn (T5).</summary>
    public static IReadOnlyList<TranscriptTurn> Conversation(Guid sessionId) =>
    [
        Turn(sessionId, 1, TurnSpeaker.Client, T1Client),
        Turn(sessionId, 2, TurnSpeaker.Advisor, T2Advisor),
        Turn(sessionId, 3, TurnSpeaker.Client, T3Client),
        Turn(sessionId, 4, TurnSpeaker.Advisor, T4Advisor),
        Turn(sessionId, 5, TurnSpeaker.Client, T5ClientInterrupted, TurnStatus.Interrupted, fullyHeard: false),
        Turn(sessionId, 6, TurnSpeaker.Advisor, T6Advisor),
    ];

    public static IReadOnlyDictionary<string, TranscriptTurn> ByLabel(IReadOnlyList<TranscriptTurn> turns) =>
        turns.ToDictionary(turn => $"T{turn.Sequence}", StringComparer.Ordinal);
}

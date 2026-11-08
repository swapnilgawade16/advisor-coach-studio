using System.Text.Json;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>A transcript turn as seen by a test: label (T1..Tn), speaker, text, and whether it may be quoted.</summary>
public sealed record ReportTurn(string Label, string Speaker, string Text, bool Quotable);

/// <summary>
/// Builds coaching-report content JSON in the shape the model must return (schema coaching-report-content.v1). Valid
/// content quotes exact substrings of the cited turns; the hallucinated variant cites an invented quote.
/// </summary>
public static class ReportContent
{
    public const string InventedQuote = "I guarantee you will never lose any money with me";

    public static readonly IReadOnlyList<string> Categories =
        ["rapport-empathy", "listening-responsiveness", "discovery-questioning", "clarity-language", "handling-concerns", "structure-next-steps"];

    /// <summary>Turns from GET /api/sessions/{id}/transcript. Client turns are quotable only when playback was confirmed.</summary>
    public static IReadOnlyList<ReportTurn> FromTranscript(JsonElement transcript) =>
    [
        .. transcript.GetProperty("turns").EnumerateArray().Select(turn =>
        {
            var text = turn.GetProperty("text").GetString()!;
            var speaker = turn.GetProperty("speaker").GetString()!;
            var quotable = turn.GetProperty("status").GetString() == "final" &&
                !string.IsNullOrWhiteSpace(text) &&
                (speaker == "advisor" || turn.GetProperty("fullyHeard").ValueKind == JsonValueKind.True);
            return new ReportTurn(turn.GetProperty("label").GetString()!, speaker, text, quotable);
        }),
    ];

    /// <summary>Turns exactly as the report model received them.</summary>
    public static IReadOnlyList<ReportTurn> FromCall(ReportCall call) =>
        [.. call.Transcript.Select(t => new ReportTurn(t.TurnId, t.Speaker, t.Text, t.Quotable))];

    /// <summary>A short exact (ordinal) substring of the turn: its first 32 characters.</summary>
    public static string Quote(ReportTurn turn) => turn.Text.Length <= 32 ? turn.Text : turn.Text[..32];

    public static string Valid(IReadOnlyList<ReportTurn> turns, IReadOnlyList<int?>? scores = null, string scenarioId = "market-decline") =>
        Build(turns, scores ?? [3, 3, 3, null, null, null], scenarioId, invent: false);

    public static string Hallucinated(IReadOnlyList<ReportTurn> turns, string scenarioId = "market-decline") =>
        Build(turns, [3, 3, 3, null, null, null], scenarioId, invent: true);

    private static string Build(IReadOnlyList<ReportTurn> turns, IReadOnlyList<int?> scores, string scenarioId, bool invent)
    {
        if (scores.Count != Categories.Count)
        {
            throw new ArgumentException("Exactly six category scores are required.", nameof(scores));
        }

        var advisor = turns.Where(t => t.Speaker == "advisor" && t.Quotable).ToList();
        var client = turns.Where(t => t.Speaker == "client" && t.Quotable).ToList();
        if (advisor.Count == 0)
        {
            throw new InvalidOperationException("At least one quotable advisor turn is required to build report content.");
        }

        object Evidence(ReportTurn turn, bool inventQuote = false) =>
            new { turnId = turn.Label, quote = inventQuote ? InventedQuote : Quote(turn), explanation = "Observed directly in this turn." };

        var skillScores = Categories.Select((category, i) => new
        {
            categoryId = category,
            score = scores[i],
            explanation = scores[i] is null ? "Not enough evidence in the transcript to assess this skill." : "Assessed from the quoted advisor turn.",
            evidence = scores[i] is null ? Array.Empty<object>() : new[] { Evidence(advisor[i % advisor.Count], invent && i == 0) },
        }).ToList();

        var content = new
        {
            summary = "You acknowledged the client's worry and moved the conversation towards a clear next step.",
            keyStrength = "You acknowledged the emotion before discussing any options.",
            priorityImprovement = "Ask more open questions about goals and time horizon before proposing a next step.",
            skillScores,
            strengths = new[]
            {
                new { title = "Acknowledged the worry", observation = "You named the client's concern early.", interpretation = (string?)null, categoryId = "rapport-empathy", evidence = new[] { Evidence(advisor[0]) } },
            },
            improvements = new[]
            {
                new
                {
                    title = "Explore goals further",
                    observation = "You moved towards next steps fairly quickly.",
                    interpretation = (string?)"This may have left the time horizon only partly explored.",
                    categoryId = "discovery-questioning",
                    evidence = new[] { Evidence(advisor[^1]) },
                    suggestedAdjustment = "Ask what the money is for and when it will be needed before discussing options.",
                },
            },
            alternativePhrases = new[]
            {
                new
                {
                    originalEvidence = Evidence(advisor[0]),
                    suggestedPhrase = "It sounds like this has been really unsettling. What worries you most right now?",
                    communicationIntention = "Acknowledge the emotion, then invite discovery.",
                    explanation = "An open question keeps the client talking about what matters to them.",
                },
            },
            missedDiscoveryQuestions = new[]
            {
                new
                {
                    question = "When will you need to draw on this money?",
                    purpose = "Understand the time horizon.",
                    relevance = "Relevant because the client raised retirement concerns.",
                    relatedTurnIds = client.Count > 0 ? new[] { client[0].Label } : [],
                },
            },
            practicePlan = new
            {
                focusAreas = new[] { "Open discovery questions" },
                exercise = "Practise asking two open questions before offering any options.",
                recommendedScenarioId = scenarioId,
            },
            warnings = Array.Empty<string>(),
        };

        return JsonSerializer.Serialize(content);
    }
}

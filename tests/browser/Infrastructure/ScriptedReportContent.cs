using System.Text.Json;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// Builds report content that passes the product's schema and grounding validation: every quote is an exact prefix of
/// a quotable turn, alternative phrases cite advisor turns only, and all six rubric categories appear once.
/// </summary>
public static class ScriptedReportContent
{
    public const string Summary = "Scripted browser-test report: you stayed calm and acknowledged the client's concern.";
    public const string StrengthTitle = "Scripted strength: calm acknowledgement";
    public const string ImprovementTitle = "Scripted improvement: ask about goals";
    public const int ScoredCategories = 4;

    private static readonly string[] Categories =
        ["rapport-empathy", "listening-responsiveness", "discovery-questioning", "clarity-language", "handling-concerns", "structure-next-steps"];

    /// <summary>Category scores used for the first <see cref="ScoredCategories"/> categories (mean 3.5).</summary>
    public static int ScoreFor(int categoryIndex) => categoryIndex % 2 == 0 ? 4 : 3;

    public static string Build(string scenarioId, IReadOnlyList<PayloadTurn> turns)
    {
        var quotable = turns.Where(turn => turn.Quotable).ToList();
        var advisor = quotable.Where(turn => turn.Speaker == "advisor").ToList();
        if (quotable.Count == 0)
        {
            throw new InvalidOperationException("The product only calls the report model when the transcript has usable advisor turns.");
        }

        var cited = advisor.Count > 0 ? advisor : quotable;

        object Evidence(PayloadTurn turn) => new
        {
            turnId = turn.TurnId,
            quote = turn.Text.Length <= 60 ? turn.Text : turn.Text[..60],
            explanation = "Scripted browser-test evidence.",
        };

        var content = new
        {
            summary = Summary,
            keyStrength = "Scripted key strength: you let the client finish before responding.",
            priorityImprovement = "Scripted priority: ask one open question about the client's goals.",
            skillScores = Categories.Select((id, index) => new
            {
                categoryId = id,
                score = index < ScoredCategories ? (int?)ScoreFor(index) : null,
                explanation = index < ScoredCategories ? "Scripted explanation grounded in the cited turn." : "Not enough evidence in this scripted report.",
                evidence = index < ScoredCategories ? new[] { Evidence(cited[index % cited.Count]) } : [],
            }).ToArray(),
            strengths = new[]
            {
                new { title = StrengthTitle, observation = "Scripted observation.", interpretation = (string?)null, categoryId = Categories[0], evidence = new[] { Evidence(cited[0]) } },
            },
            improvements = new[]
            {
                new
                {
                    title = ImprovementTitle,
                    observation = "Scripted observation.",
                    interpretation = (string?)null,
                    categoryId = Categories[2],
                    evidence = new[] { Evidence(cited[^1]) },
                    suggestedAdjustment = "Ask what the money is for and when it will be needed.",
                },
            },
            alternativePhrases = advisor.Take(1).Select(turn => new
            {
                originalEvidence = Evidence(turn),
                suggestedPhrase = "It sounds like this has been worrying you. What matters most to you right now?",
                communicationIntention = "Acknowledge the emotion, then invite discovery.",
                explanation = "Scripted explanation.",
            }).ToArray(),
            missedDiscoveryQuestions = new[]
            {
                new { question = "What are you hoping this money will do for you, and when?", purpose = "Understand goals and time horizon.", relevance = "Scripted relevance.", relatedTurnIds = Array.Empty<string>() },
            },
            practicePlan = new
            {
                focusAreas = new[] { "Scripted focus area" },
                exercise = "Scripted exercise: acknowledge the concern before explaining.",
                recommendedScenarioId = scenarioId,
            },
            warnings = Array.Empty<string>(),
        };

        return JsonSerializer.Serialize(content);
    }
}

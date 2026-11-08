using System.Text.Json;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;

namespace AdvisorCoachStudio.Web.Infrastructure.Simulation;

/// <summary>
/// DevelopmentSimulation report model: builds deterministic, schema-valid content from the transcript JSON it is given.
/// Quotes are exact substrings so the normal validation path is exercised. Every report is labelled as simulated.
/// </summary>
public sealed class SimulatedReportModelClient : IReportModelClient
{
    private static readonly string[] Categories =
        ["rapport-empathy", "listening-responsiveness", "discovery-questioning", "clarity-language", "handling-concerns", "structure-next-steps"];

    public string DeploymentName => "simulation";

    public Task<ReportModelResult> CompleteAsync(IReadOnlyList<ReportChatMessage> messages, JsonObject schema, CancellationToken cancellationToken)
    {
        var user = messages.Last(m => m.Role == "user").Content;
        var payload = JsonNode.Parse(user[(user.IndexOf('\n', StringComparison.Ordinal) + 1)..])!;
        var scenarioId = payload["scenario"]!["id"]!.GetValue<string>();
        var advisorTurns = payload["transcript"]!.AsArray()
            .Where(t => t!["speaker"]!.GetValue<string>() == "advisor" && t["quotable"]!.GetValue<bool>())
            .Select(t => (Id: t!["turnId"]!.GetValue<string>(), Text: t["text"]!.GetValue<string>()))
            .ToList();

        object Evidence((string Id, string Text) turn) => new
        {
            turnId = turn.Id,
            quote = turn.Text.Length > 40 ? turn.Text[..40] : turn.Text,
            explanation = ProviderReadiness.SimulationLabel,
        };

        var first = advisorTurns[0];
        var content = new
        {
            summary = $"{ProviderReadiness.SimulationLabel}. This report demonstrates the layout only and does not evaluate your conversation.",
            keyStrength = (string?)null,
            priorityImprovement = (string?)null,
            skillScores = Categories.Select((id, index) => new
            {
                categoryId = id,
                score = index < 3 && advisorTurns.Count >= 3 ? (int?)3 : null,
                explanation = ProviderReadiness.SimulationLabel,
                evidence = index < 3 && advisorTurns.Count >= 3 ? new[] { Evidence(advisorTurns[index % advisorTurns.Count]) } : [],
            }).ToArray(),
            strengths = new[]
            {
                new { title = "Simulated strength", observation = ProviderReadiness.SimulationLabel, interpretation = (string?)null, categoryId = Categories[0], evidence = new[] { Evidence(first) } },
            },
            improvements = new[]
            {
                new { title = "Simulated improvement", observation = ProviderReadiness.SimulationLabel, interpretation = (string?)null, categoryId = Categories[2], evidence = new[] { Evidence(first) }, suggestedAdjustment = "Simulated suggestion: ask one open question about the client's goals." },
            },
            alternativePhrases = new[]
            {
                new { originalEvidence = Evidence(first), suggestedPhrase = "It sounds like this has been worrying you. What matters most to you right now?", communicationIntention = "Acknowledge emotion, then invite discovery.", explanation = ProviderReadiness.SimulationLabel },
            },
            missedDiscoveryQuestions = new[]
            {
                new { question = "What are you hoping this money will do for you, and when?", purpose = "Understand goals and time horizon.", relevance = ProviderReadiness.SimulationLabel, relatedTurnIds = Array.Empty<string>() },
            },
            practicePlan = new { focusAreas = new[] { "Simulated focus area" }, exercise = "Simulated exercise: practise acknowledging the concern before explaining.", recommendedScenarioId = scenarioId },
            warnings = new[] { ProviderReadiness.SimulationLabel },
        };

        return Task.FromResult(new ReportModelResult(JsonSerializer.Serialize(content), "stop", null));
    }
}

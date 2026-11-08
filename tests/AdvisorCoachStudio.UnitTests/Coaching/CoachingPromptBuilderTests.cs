using System.Text.Json;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Coaching;

namespace AdvisorCoachStudio.UnitTests.Coaching;

public sealed class CoachingPromptBuilderTests
{
    [Fact]
    public void SystemPromptStatesTheLengthLimitsTheValidatorEnforces()
    {
        var prompt = CoachingPromptBuilder.SystemPrompt([TestScenarios.MarketDecline]);

        Assert.Contains($"summary: at most {ReportValidator.MaxSummary} characters", prompt, StringComparison.Ordinal);
        Assert.Contains($"Titles and focus areas: at most {ReportValidator.MaxShortText} characters", prompt, StringComparison.Ordinal);
        Assert.Contains($"Quotes: at most {ReportValidator.MaxQuote} characters", prompt, StringComparison.Ordinal);
        Assert.Contains($"practice exercise: at most {ReportValidator.MaxText} characters", prompt, StringComparison.Ordinal);
        Assert.Contains($"At most {ReportValidator.MaxEvidencePerItem} evidence items", prompt, StringComparison.Ordinal);
    }

    /// <summary>Regression: escaped apostrophes (\u0027, \u2019) in the prompt JSON were copied into quotes as U+0019 by gpt-5.1.</summary>
    [Fact]
    public void TranscriptJsonKeepsPunctuationAsLiteralCharacters()
    {
        const string client = "I\u2019m worried \u2014 we\u2019ve lost a lot.";
        const string advisor = "That's understandable, and it's worth a \"pause\" before deciding.";
        var sessionId = Guid.NewGuid();
        TranscriptTurn[] conversation =
        [
            TestTranscripts.Turn(sessionId, 1, TurnSpeaker.Client, client),
            TestTranscripts.Turn(sessionId, 2, TurnSpeaker.Advisor, advisor),
        ];

        var (message, labels) = CoachingPromptBuilder.UserMessage(TestScenarios.Shipped(TestScenarios.MarketDecline), Difficulty.Intermediate, conversation);

        Assert.Contains(client, message, StringComparison.Ordinal);
        Assert.Contains("That's understandable, and it's worth a \\\"pause\\\" before deciding.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", message, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(message[message.IndexOf('{', StringComparison.Ordinal)..]);
        Assert.Equal(advisor, payload.RootElement.GetProperty("transcript")[1].GetProperty("text").GetString());
        Assert.Equal(["T1", "T2"], labels.Keys.Order(StringComparer.Ordinal));
    }
}

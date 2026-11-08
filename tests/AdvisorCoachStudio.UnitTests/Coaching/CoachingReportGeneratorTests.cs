using System.Text.Json;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;
using AdvisorCoachStudio.Web.Prompts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdvisorCoachStudio.UnitTests.Coaching;

/// <summary>Report generation, validation, and the single bounded correction request (spec section 11.4).</summary>
public sealed class CoachingReportGeneratorTests
{
    private const string InventedQuote = "I promise the market will recover";

    private readonly FakeTimeProvider _time = new();
    private readonly PracticeSession _session = new(
        Guid.NewGuid(),
        FakeTimeProvider.DefaultStart.AddHours(8),
        TestScenarios.Shipped(TestScenarios.MarketDecline),
        Difficulty.Intermediate,
        RoleplayPromptBuilder.Version,
        isSimulated: false,
        FakeTimeProvider.DefaultStart.UtcDateTime);

    [Fact]
    public async Task ValidOutputProducesAReportWithServerAssignedFields()
    {
        var turns = TestTranscripts.Conversation(_session.Id);
        var client = new ScriptedReportModelClient(Output(TestReportContent.Valid()));

        var report = await Generate(client, turns);

        Assert.Equal(_session.Id, report.SessionId);
        Assert.Equal(TestScenarios.MarketDecline, report.ScenarioId);
        Assert.Equal(_session.Scenario.Version, report.ScenarioVersion);
        Assert.Equal(1, report.TranscriptRevision);
        Assert.Equal("synthetic-hash", report.TranscriptHash);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, report.GeneratedAtUtc);
        Assert.Equal(Rubric.SchemaVersion, report.SchemaVersion);
        Assert.Equal(CoachingPromptBuilder.Version, report.PromptVersion);
        Assert.Equal(Rubric.Version, report.RubricVersion);
        Assert.Equal(ScriptedReportModelClient.Deployment, report.ModelDeployment);
        Assert.Equal(AssessmentStatus.SufficientEvidence, report.AssessmentStatus);
        Assert.Equal(TestReportContent.ExpectedOverallScore, report.OverallScore);
        Assert.Equal(TestReportContent.ExpectedScoredCategories, report.ScoredCategoryCount);
        Assert.Equal(3, report.UsableAdvisorTurnCount);
        Assert.False(report.IsSimulated);
        Assert.Equal(TestReportContent.ToJson(TestReportContent.Valid()), TestReportContent.ToJson(report.Content));
        Assert.Equal(turns.Count, report.TurnLabels.Count);
        Assert.All(turns, turn => Assert.Equal(turn.Id, report.TurnLabels[$"T{turn.Sequence}"]));

        var call = Assert.Single(client.Calls);
        Assert.Equal(new[] { "system", "user" }, call.Select(message => message.Role));
        Assert.Contains("market-decline, fees-and-trust, first-time-investor", call[0].Content);
        Assert.Contains("\"turnId\":\"T2\"", call[1].Content);
        Assert.Same(TestReportSchema.Shared.ProviderSchema, Assert.Single(client.Schemas));
    }

    [Fact]
    public async Task ModelSuppliedOverallScoreIsRejectedAndRecomputedServerSide()
    {
        var withScore = JsonNode.Parse(TestReportContent.ToJson(TestReportContent.Valid()))!.AsObject();
        withScore["overallScore"] = 5;
        var client = new ScriptedReportModelClient(new ReportModelResult(withScore.ToJsonString(), "stop", null), Output(TestReportContent.Valid()));

        var report = await Generate(client, TestTranscripts.Conversation(_session.Id));

        Assert.Equal(2, client.Calls.Count);
        Assert.StartsWith("Your previous JSON was rejected by validation", client.Calls[1][3].Content);
        Assert.Equal(TestReportContent.ExpectedOverallScore, report.OverallScore);
    }

    [Fact]
    public async Task InventedQuoteGetsOneCorrectionRequestThenSucceeds()
    {
        var invalid = Output(WithInventedQuote());
        var client = new ScriptedReportModelClient(invalid, Output(TestReportContent.Valid()));

        var report = await Generate(client, TestTranscripts.Conversation(_session.Id));

        Assert.Equal(2, client.Calls.Count);
        var correction = client.Calls[1];
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, correction.Select(message => message.Role));
        Assert.Equal(client.Calls[0], correction.Take(2));
        Assert.Equal(invalid.Content, correction[2].Content);
        Assert.StartsWith("Your previous JSON was rejected by validation", correction[3].Content);
        Assert.Contains("strengths[0].evidence[0].quote: is not an exact substring of turn T2.", correction[3].Content);
        Assert.DoesNotContain(InventedQuote, correction[3].Content);
        Assert.Equal(AssessmentStatus.SufficientEvidence, report.AssessmentStatus);
    }

    [Fact]
    public async Task TwoInvalidOutputsFailWithInvalidReportOutput()
    {
        var client = new ScriptedReportModelClient(Output(WithInventedQuote()), Output(WithInventedQuote()));

        var error = await Assert.ThrowsAsync<ReportGenerationException>(() => Generate(client, TestTranscripts.Conversation(_session.Id)));

        Assert.Equal("invalid_report_output", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(2, client.Calls.Count);
    }

    /// <summary>Regression: gpt-5.1 copied apostrophes as U+0019 or curly quotes, so every quote failed validation twice.</summary>
    [Fact]
    public async Task QuotesWithDifferentApostrophesAreStoredAsExactTranscriptTextWithoutACorrectionRequest()
    {
        var valid = TestReportContent.Valid();
        var garbled = valid with
        {
            SkillScores = [.. valid.SkillScores.Take(5), TestReportContent.Score("structure-next-steps", 4, TestReportContent.Evidence("T6", "Let\u0019s book a review next week"))],
            Strengths = [valid.Strengths[0] with { Evidence = [TestReportContent.Evidence("T1", "I\u2019m really worried.")] }],
        };
        var client = new ScriptedReportModelClient(Output(garbled));
        var turns = TestTranscripts.Conversation(_session.Id);

        var report = await Generate(client, turns);

        Assert.Single(client.Calls);
        Assert.Equal("Let's book a review next week", report.Content.SkillScores[5].Evidence[0].Quote);
        Assert.Equal("I'm really worried.", report.Content.Strengths[0].Evidence[0].Quote);
        Assert.Empty(ReportValidator.Validate(report.Content, TestTranscripts.ByLabel(turns), TestScenarios.Catalog));
    }

    [Theory]
    [InlineData("length", "truncated")]
    [InlineData("stop", "not valid JSON")]
    public async Task TruncatedOrMalformedOutputGetsOneCorrectionRequest(string finishReason, string expectedProblem)
    {
        var partial = TestReportContent.ToJson(TestReportContent.Valid())[..200];
        var client = new ScriptedReportModelClient(new ReportModelResult(partial, finishReason, null), Output(TestReportContent.Valid()));

        var report = await Generate(client, TestTranscripts.Conversation(_session.Id));

        Assert.Equal(2, client.Calls.Count);
        Assert.Contains(expectedProblem, client.Calls[1][3].Content);
        Assert.Equal(TestReportContent.ExpectedOverallScore, report.OverallScore);
    }

    [Fact]
    public async Task TruncatedOutputWithoutContentCannotBeCorrected()
    {
        var client = new ScriptedReportModelClient(new ReportModelResult(null, "length", null));

        var error = await Assert.ThrowsAsync<ReportGenerationException>(() => Generate(client, TestTranscripts.Conversation(_session.Id)));

        Assert.Equal("invalid_report_output", error.Code);
        Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData(null, "stop", "I can't help with that.", "report_refused", false)]
    [InlineData(null, "content_filter", null, "report_content_filtered", false)]
    [InlineData("", "stop", null, "report_empty_output", true)]
    public async Task NonCorrectableOutcomesFailWithoutACorrectionRequest(string? content, string finishReason, string? refusal, string expectedCode, bool retryable)
    {
        var client = new ScriptedReportModelClient(new ReportModelResult(content, finishReason, refusal));

        var error = await Assert.ThrowsAsync<ReportGenerationException>(() => Generate(client, TestTranscripts.Conversation(_session.Id)));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task NoUsableAdvisorTurnsYieldLimitedEvidenceWithoutCallingTheModel()
    {
        TranscriptTurn[] turns =
        [
            TestTranscripts.Turn(_session.Id, 1, TurnSpeaker.Client, TestTranscripts.T1Client),
            TestTranscripts.Turn(_session.Id, 2, TurnSpeaker.Advisor, "", TurnStatus.Failed),
            TestTranscripts.Turn(_session.Id, 3, TurnSpeaker.Advisor, "Partial words", TurnStatus.Pending),
        ];
        var client = new ScriptedReportModelClient();

        var report = await Generate(client, turns);

        Assert.Empty(client.Calls);
        Assert.Equal(AssessmentStatus.LimitedEvidence, report.AssessmentStatus);
        Assert.Null(report.OverallScore);
        Assert.Equal(0, report.ScoredCategoryCount);
        Assert.Equal(0, report.UsableAdvisorTurnCount);
        Assert.Equal("none", report.ModelDeployment);
        Assert.Empty(report.TurnLabels);
        Assert.Equal(Rubric.Categories.Select(category => category.Id), report.Content.SkillScores.Select(score => score.CategoryId));
        Assert.All(report.Content.SkillScores, score => Assert.Null(score.Score));
        Assert.Equal(TestScenarios.MarketDecline, report.Content.PracticePlan.RecommendedScenarioId);
        Assert.Empty(ReportValidator.Validate(report.Content, new Dictionary<string, TranscriptTurn>(), TestScenarios.Catalog));
        using var document = JsonDocument.Parse(TestReportContent.ToJson(report.Content));
        Assert.Empty(TestReportSchema.Shared.Validate(document.RootElement));
    }

    [Fact]
    public async Task FewerThanThreeUsableAdvisorTurnsGiveLimitedEvidenceEvenWithScores()
    {
        var turns = TestTranscripts.Conversation(_session.Id).Take(5).ToList();
        var content = TestReportContent.Valid();
        content = content with
        {
            SkillScores = [.. content.SkillScores.Take(5), TestReportContent.Score("structure-next-steps", 4, TestReportContent.Evidence("T4", "Before we decide"))],
        };
        var client = new ScriptedReportModelClient(Output(content));

        var report = await Generate(client, turns);

        Assert.Equal(2, report.UsableAdvisorTurnCount);
        Assert.Equal(5, report.ScoredCategoryCount);
        Assert.Null(report.OverallScore);
        Assert.Equal(AssessmentStatus.LimitedEvidence, report.AssessmentStatus);
    }

    private Task<CoachingReport> Generate(ScriptedReportModelClient client, IReadOnlyList<TranscriptTurn> turns) =>
        new CoachingReportGenerator(client, TestReportSchema.Shared, TestScenarios.Catalog, _time, NullLogger<CoachingReportGenerator>.Instance)
            .GenerateAsync(_session, turns, revision: 1, transcriptHash: "synthetic-hash", TestContext.Current.CancellationToken);

    private static ReportModelResult Output(CoachingReportContent content) => new(TestReportContent.ToJson(content), "stop", null);

    private static CoachingReportContent WithInventedQuote()
    {
        var content = TestReportContent.Valid();
        return content with { Strengths = [content.Strengths[0] with { Evidence = [TestReportContent.Evidence("T2", InventedQuote)] }] };
    }
}

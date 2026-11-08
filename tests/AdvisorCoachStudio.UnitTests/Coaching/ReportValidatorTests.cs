using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Coaching;

namespace AdvisorCoachStudio.UnitTests.Coaching;

/// <summary>Evidence grounding and identifier rules (spec section 11.3).</summary>
public sealed partial class ReportValidatorTests
{
    private const string SixCategories = "skillScores: must contain each of the six rubric categories exactly once.";

    private readonly IReadOnlyList<TranscriptTurn> _turns = TestTranscripts.Conversation(Guid.NewGuid());

    [Fact]
    public void ValidContentPasses() => Assert.Empty(Validate(TestReportContent.Valid()));

    [Theory]
    [InlineData("stressful day")]
    [InlineData("Stressful morning")]
    [InlineData("stressful  morning")]
    [InlineData("a stressful morning for you. Thanks.")]
    public void QuoteThatIsNotAnExactSubstringIsRejected(string quote)
    {
        var errors = Validate(WithStrengthEvidence(TestReportContent.Evidence("T2", quote)));

        Assert.Equal("strengths[0].evidence[0].quote: is not an exact substring of turn T2.", Assert.Single(errors));
    }

    [Fact]
    public void EmptyQuoteIsRejected() =>
        Assert.Equal("strengths[0].evidence[0].quote: must not be empty.", Assert.Single(Validate(WithStrengthEvidence(TestReportContent.Evidence("T2", " ")))));

    [Fact]
    public void UnknownTurnIdIsRejected() =>
        Assert.Equal("strengths[0].evidence[0].turnId: unknown turn id 'T99'.", Assert.Single(Validate(WithStrengthEvidence(TestReportContent.Evidence("T99", "stressful morning")))));

    [Fact]
    public void QuotingAnInterruptedClientTurnIsRejected()
    {
        var errors = Validate(WithStrengthEvidence(TestReportContent.Evidence("T5", "In about five years")));

        Assert.Equal("strengths[0].evidence[0].turnId: turn T5 is not quotable (pending, failed, or not fully heard).", Assert.Single(errors));
    }

    [Fact]
    public void QuotingAClientTurnWithoutConfirmedPlaybackIsRejected()
    {
        var turns = _turns.Select(turn => turn.Sequence == 3 ? turn with { FullyHeard = null } : turn).ToList();

        var errors = ReportValidator.Validate(TestReportContent.Valid(), TestTranscripts.ByLabel(turns), TestScenarios.Catalog);

        Assert.Equal("improvements[0].evidence[0].turnId: turn T3 is not quotable (pending, failed, or not fully heard).", Assert.Single(errors));
    }

    [Theory]
    [InlineData(TurnStatus.Pending)]
    [InlineData(TurnStatus.Failed)]
    public void QuotingAnUnfinishedAdvisorTurnIsRejected(TurnStatus status)
    {
        var turns = _turns.Select(turn => turn.Sequence == 6 ? turn with { Status = status } : turn).ToList();

        var errors = ReportValidator.Validate(TestReportContent.Valid(), TestTranscripts.ByLabel(turns), TestScenarios.Catalog);

        Assert.Equal("skillScores[5].evidence[0].turnId: turn T6 is not quotable (pending, failed, or not fully heard).", Assert.Single(errors));
    }

    [Fact]
    public void AlternativePhraseMustQuoteAnAdvisorTurn()
    {
        var content = TestReportContent.Valid();
        content = content with { AlternativePhrases = [content.AlternativePhrases[0] with { OriginalEvidence = TestReportContent.Evidence("T3", "My partner thinks") }] };

        Assert.Equal("alternativePhrases[0].originalEvidence.turnId: turn T3 is not an advisor turn.", Assert.Single(Validate(content)));
    }

    [Fact]
    public void DuplicateCategoryIsRejected()
    {
        var content = TestReportContent.Valid();
        var scores = content.SkillScores.ToList();
        scores[4] = scores[0];

        Assert.Equal(SixCategories, Assert.Single(Validate(content with { SkillScores = scores })));
    }

    [Fact]
    public void MissingCategoryIsRejected()
    {
        var content = TestReportContent.Valid();

        Assert.Equal(SixCategories, Assert.Single(Validate(content with { SkillScores = content.SkillScores.Take(5).ToList() })));
    }

    [Fact]
    public void UnknownCategoryIsRejected()
    {
        var content = TestReportContent.Valid();
        var scores = content.SkillScores.ToList();
        scores[4] = scores[4] with { CategoryId = "tone-of-voice" };

        var errors = Validate(content with { SkillScores = scores });

        Assert.Contains("skillScores[4].categoryId: unknown category.", errors);
        Assert.Contains(SixCategories, errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void ScoreOutsideOneToFiveIsRejected(int score)
    {
        var content = TestReportContent.Valid();
        var scores = content.SkillScores.ToList();
        scores[0] = scores[0] with { Score = score };

        Assert.Equal("skillScores[0].score: must be an integer from 1 to 5, or null.", Assert.Single(Validate(content with { SkillScores = scores })));
    }

    [Fact]
    public void ScoredCategoryWithoutEvidenceIsRejected()
    {
        var content = TestReportContent.Valid();
        var scores = content.SkillScores.ToList();
        scores[0] = scores[0] with { Evidence = [] };

        Assert.Equal("skillScores[0].evidence: at least one transcript reference is required.", Assert.Single(Validate(content with { SkillScores = scores })));
    }

    [Fact]
    public void UnscoredCategoriesNeedNoEvidence()
    {
        var content = TestReportContent.Valid() with
        {
            SkillScores = Rubric.Categories.Select(category => TestReportContent.Score(category.Id, null)).ToList(),
        };

        Assert.Empty(Validate(content));
    }

    [Fact]
    public void UnsupportedRecommendedScenarioIsRejected()
    {
        var content = TestReportContent.Valid();
        content = content with { PracticePlan = content.PracticePlan with { RecommendedScenarioId = "retirement-planning" } };

        Assert.Equal("practicePlan.recommendedScenarioId: unsupported scenario.", Assert.Single(Validate(content)));
    }

    [Fact]
    public void MissedQuestionWithUnknownRelatedTurnIsRejected()
    {
        var content = TestReportContent.Valid();
        content = content with { MissedDiscoveryQuestions = [content.MissedDiscoveryQuestions[0] with { RelatedTurnIds = ["T3", "T42"] }] };

        Assert.Equal("missedDiscoveryQuestions[0].relatedTurnIds: unknown turn id 'T42'.", Assert.Single(Validate(content)));
    }

    [Fact]
    public void ErrorsNeverEchoTranscriptOrQuoteText()
    {
        var errors = Validate(WithStrengthEvidence(TestReportContent.Evidence("T2", "I promise the market will recover")));

        Assert.NotEmpty(errors);
        Assert.All(errors, error =>
        {
            Assert.DoesNotContain("promise the market", error);
            Assert.DoesNotContain("stressful", error);
        });
    }

    private List<string> Validate(CoachingReportContent content) =>
        ReportValidator.Validate(content, TestTranscripts.ByLabel(_turns), TestScenarios.Catalog);

    private static CoachingReportContent WithStrengthEvidence(ReportEvidence evidence)
    {
        var content = TestReportContent.Valid();
        return content with { Strengths = [content.Strengths[0] with { Evidence = [evidence] }] };
    }
}

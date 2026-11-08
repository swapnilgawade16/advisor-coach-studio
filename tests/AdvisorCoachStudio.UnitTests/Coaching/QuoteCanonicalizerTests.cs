using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Coaching;

namespace AdvisorCoachStudio.UnitTests.Coaching;

/// <summary>Quotes that differ from the transcript only in punctuation style are mapped back to the exact transcript text.</summary>
public sealed class QuoteCanonicalizerTests
{
    private const string CurlyClient = "I\u2019m worried \u2014 we\u2019ve lost a lot.";
    private const string StraightAdvisor = "That's understandable, and it's worth a \"pause\" before deciding.";

    [Theory]
    [InlineData(CurlyClient, "I'm worried", "I\u2019m worried")]
    [InlineData(CurlyClient, "we've lost a lot.", "we\u2019ve lost a lot.")]
    [InlineData(CurlyClient, "worried - we\u2019ve", "worried \u2014 we\u2019ve")]
    [InlineData(StraightAdvisor, "That\u2019s understandable", "That's understandable")]
    [InlineData(StraightAdvisor, "That\u0019s understandable", "That's understandable")]
    [InlineData(StraightAdvisor, "a \u201Cpause\u201D before", "a \"pause\" before")]
    public void PunctuationStyleDifferencesMapToTheExactTranscriptText(string text, string quote, string expected) =>
        Assert.Equal(expected, QuoteCanonicalizer.FindInTranscript(text, quote));

    [Theory]
    [InlineData("that's understandable")]
    [InlineData("That is understandable")]
    [InlineData("That's  understandable")]
    [InlineData("It's understandable, and that's worth")]
    public void OtherDifferencesAreNotMatched(string quote) =>
        Assert.Null(QuoteCanonicalizer.FindInTranscript(StraightAdvisor, quote));

    [Fact]
    public void ExactAndUnknownTurnEvidenceIsReturnedUnchanged()
    {
        var turns = TestTranscripts.ByLabel(TestTranscripts.Conversation(Guid.NewGuid()));
        var exact = TestReportContent.Evidence("T2", "a stressful morning for you");
        var unknown = TestReportContent.Evidence("T99", "I\u2019m really worried");

        Assert.Same(exact, QuoteCanonicalizer.Canonicalize(exact, turns));
        Assert.Same(unknown, QuoteCanonicalizer.Canonicalize(unknown, turns));
    }

    [Fact]
    public void EveryEvidenceSectionIsCanonicalizedAndThenPassesStrictValidation()
    {
        var conversation = TestTranscripts.Conversation(Guid.NewGuid());
        var turns = TestTranscripts.ByLabel(conversation);
        var valid = TestReportContent.Valid();
        var garbled = valid with
        {
            SkillScores = [.. valid.SkillScores.Take(5), TestReportContent.Score("structure-next-steps", 4, TestReportContent.Evidence("T6", "Let\u0019s book a review"))],
            Strengths = [valid.Strengths[0] with { Evidence = [TestReportContent.Evidence("T1", "I\u2019m really worried.")] }],
            Improvements = [valid.Improvements[0] with { Evidence = [TestReportContent.Evidence("T6", "Let\u2019s book")] }],
            AlternativePhrases = [valid.AlternativePhrases[0] with { OriginalEvidence = TestReportContent.Evidence("T6", "Let\u2018s book a review next week") }],
        };
        Assert.NotEmpty(ReportValidator.Validate(garbled, turns, TestScenarios.Catalog));

        var canonical = QuoteCanonicalizer.Canonicalize(garbled, turns);

        Assert.Equal("Let's book a review", canonical.SkillScores[5].Evidence[0].Quote);
        Assert.Equal("I'm really worried.", canonical.Strengths[0].Evidence[0].Quote);
        Assert.Equal("Let's book", canonical.Improvements[0].Evidence[0].Quote);
        Assert.Equal("Let's book a review next week", canonical.AlternativePhrases[0].OriginalEvidence.Quote);
        Assert.Same(valid.SkillScores[0].Evidence[0], canonical.SkillScores[0].Evidence[0]);
        Assert.Empty(ReportValidator.Validate(canonical, turns, TestScenarios.Catalog));
    }
}

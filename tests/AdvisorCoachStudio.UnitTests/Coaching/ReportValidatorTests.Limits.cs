using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Coaching;

namespace AdvisorCoachStudio.UnitTests.Coaching;

/// <summary>Bounded string and array lengths, enforced server-side because strict structured output cannot.</summary>
public sealed partial class ReportValidatorTests
{
    public static TheoryData<string, string> OverLimitCases => new()
    {
        { "summary", "summary: exceeds 1200 characters." },
        { "keyStrength", "keyStrength: exceeds 700 characters." },
        { "strengthTitle", "strengths[0].title: exceeds 160 characters." },
        { "skillExplanation", "skillScores[0].explanation: exceeds 700 characters." },
        { "evidenceExplanation", "strengths[0].evidence[0].explanation: exceeds 700 characters." },
        { "quote", "strengths[0].evidence[0].quote: too long." },
        { "suggestedPhrase", "alternativePhrases[0].suggestedPhrase: exceeds 700 characters." },
        { "focusArea", "practicePlan.focusAreas[0]: exceeds 160 characters." },
        { "warning", "warnings[0]: exceeds 700 characters." },
        { "strengths", "strengths: at most 4 entries are allowed." },
        { "improvements", "improvements: at most 4 entries are allowed." },
        { "alternativePhrases", "alternativePhrases: at most 3 entries are allowed." },
        { "missedDiscoveryQuestions", "missedDiscoveryQuestions: at most 4 entries are allowed." },
        { "focusAreas", "practicePlan.focusAreas: at most 2 entries are allowed." },
        { "warnings", "warnings: at most 5 entries are allowed." },
        { "evidence", "skillScores[0].evidence: at most 4 entries are allowed." },
        { "relatedTurnIds", "missedDiscoveryQuestions[0].relatedTurnIds: at most 4 entries are allowed." },
    };

    [Theory]
    [MemberData(nameof(OverLimitCases))]
    public void OverLengthStringsAndArraysAreRejected(string limit, string expectedError) =>
        Assert.Equal(expectedError, Assert.Single(Validate(OverLimit(TestReportContent.Valid(), limit))));

    [Fact]
    public void ValuesExactlyAtTheLimitsAreAccepted()
    {
        var content = TestReportContent.Valid();
        content = content with
        {
            Summary = Text(ReportValidator.MaxSummary),
            Strengths = Repeat(content.Strengths[0] with { Title = Text(ReportValidator.MaxShortText) }, ReportValidator.MaxStrengths),
            AlternativePhrases = Repeat(content.AlternativePhrases[0], ReportValidator.MaxAlternativePhrases),
            PracticePlan = content.PracticePlan with { FocusAreas = Repeat(Text(ReportValidator.MaxShortText), ReportValidator.MaxFocusAreas) },
            Warnings = Repeat(Text(ReportValidator.MaxText), ReportValidator.MaxWarnings),
        };

        Assert.Empty(Validate(content));
    }

    private static CoachingReportContent OverLimit(CoachingReportContent c, string limit) => limit switch
    {
        "summary" => c with { Summary = Text(ReportValidator.MaxSummary + 1) },
        "keyStrength" => c with { KeyStrength = Text(ReportValidator.MaxText + 1) },
        "strengthTitle" => c with { Strengths = [c.Strengths[0] with { Title = Text(ReportValidator.MaxShortText + 1) }] },
        "skillExplanation" => c with { SkillScores = [c.SkillScores[0] with { Explanation = Text(ReportValidator.MaxText + 1) }, .. c.SkillScores.Skip(1)] },
        "evidenceExplanation" => c with { Strengths = [c.Strengths[0] with { Evidence = [c.Strengths[0].Evidence[0] with { Explanation = Text(ReportValidator.MaxText + 1) }] }] },
        "quote" => c with { Strengths = [c.Strengths[0] with { Evidence = [c.Strengths[0].Evidence[0] with { Quote = Text(ReportValidator.MaxQuote + 1) }] }] },
        "suggestedPhrase" => c with { AlternativePhrases = [c.AlternativePhrases[0] with { SuggestedPhrase = Text(ReportValidator.MaxText + 1) }] },
        "focusArea" => c with { PracticePlan = c.PracticePlan with { FocusAreas = [Text(ReportValidator.MaxShortText + 1)] } },
        "warning" => c with { Warnings = [Text(ReportValidator.MaxText + 1)] },
        "strengths" => c with { Strengths = Repeat(c.Strengths[0], ReportValidator.MaxStrengths + 1) },
        "improvements" => c with { Improvements = Repeat(c.Improvements[0], ReportValidator.MaxImprovements + 1) },
        "alternativePhrases" => c with { AlternativePhrases = Repeat(c.AlternativePhrases[0], ReportValidator.MaxAlternativePhrases + 1) },
        "missedDiscoveryQuestions" => c with { MissedDiscoveryQuestions = Repeat(c.MissedDiscoveryQuestions[0], ReportValidator.MaxMissedQuestions + 1) },
        "focusAreas" => c with { PracticePlan = c.PracticePlan with { FocusAreas = Repeat("Checking understanding", ReportValidator.MaxFocusAreas + 1) } },
        "warnings" => c with { Warnings = Repeat("Synthetic warning.", ReportValidator.MaxWarnings + 1) },
        "evidence" => c with { SkillScores = [c.SkillScores[0] with { Evidence = Repeat(c.SkillScores[0].Evidence[0], ReportValidator.MaxEvidencePerItem + 1) }, .. c.SkillScores.Skip(1)] },
        "relatedTurnIds" => c with { MissedDiscoveryQuestions = [c.MissedDiscoveryQuestions[0] with { RelatedTurnIds = Repeat("T3", ReportValidator.MaxEvidencePerItem + 1) }] },
        _ => throw new ArgumentOutOfRangeException(nameof(limit), limit, "Unknown limit case."),
    };

    private static string Text(int length) => new('x', length);

    private static List<T> Repeat<T>(T item, int count) => Enumerable.Repeat(item, count).ToList();
}

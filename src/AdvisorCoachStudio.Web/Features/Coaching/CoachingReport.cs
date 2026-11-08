using System.Text.Json.Serialization;
using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.Web.Features.Coaching;

// ---- Model-generated content (validated against Schemas/coaching-report-content.v1.schema.json) ----

public sealed record ReportEvidence
{
    public required string TurnId { get; init; }
    public required string Quote { get; init; }
    public required string Explanation { get; init; }
}

public sealed record SkillScoreContent
{
    public required string CategoryId { get; init; }
    public required int? Score { get; init; }
    public required string Explanation { get; init; }
    public required IReadOnlyList<ReportEvidence> Evidence { get; init; }
}

public sealed record StrengthContent
{
    public required string Title { get; init; }
    public required string Observation { get; init; }
    public required string? Interpretation { get; init; }
    public required string CategoryId { get; init; }
    public required IReadOnlyList<ReportEvidence> Evidence { get; init; }
}

public sealed record ImprovementContent
{
    public required string Title { get; init; }
    public required string Observation { get; init; }
    public required string? Interpretation { get; init; }
    public required string CategoryId { get; init; }
    public required IReadOnlyList<ReportEvidence> Evidence { get; init; }
    public required string SuggestedAdjustment { get; init; }
}

public sealed record AlternativePhraseContent
{
    public required ReportEvidence OriginalEvidence { get; init; }
    public required string SuggestedPhrase { get; init; }
    public required string CommunicationIntention { get; init; }
    public required string Explanation { get; init; }
}

public sealed record MissedQuestionContent
{
    public required string Question { get; init; }
    public required string Purpose { get; init; }
    public required string Relevance { get; init; }
    public required IReadOnlyList<string> RelatedTurnIds { get; init; }
}

public sealed record PracticePlanContent
{
    public required IReadOnlyList<string> FocusAreas { get; init; }
    public required string Exercise { get; init; }
    public required string RecommendedScenarioId { get; init; }
}

/// <summary>The only part of a report the model generates.</summary>
public sealed record CoachingReportContent
{
    public required string Summary { get; init; }
    public required string? KeyStrength { get; init; }
    public required string? PriorityImprovement { get; init; }
    public required IReadOnlyList<SkillScoreContent> SkillScores { get; init; }
    public required IReadOnlyList<StrengthContent> Strengths { get; init; }
    public required IReadOnlyList<ImprovementContent> Improvements { get; init; }
    public required IReadOnlyList<AlternativePhraseContent> AlternativePhrases { get; init; }
    public required IReadOnlyList<MissedQuestionContent> MissedDiscoveryQuestions { get; init; }
    public required PracticePlanContent PracticePlan { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

// ---- Full report: server-assigned identity, versions, and computed scores + validated content ----

public sealed record CoachingReport
{
    public required string SchemaVersion { get; init; }
    public required Guid SessionId { get; init; }
    public required string ScenarioId { get; init; }
    public required string ScenarioVersion { get; init; }
    public required int TranscriptRevision { get; init; }
    public required string? TranscriptHash { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
    public required string PromptVersion { get; init; }
    public required string RubricVersion { get; init; }
    public required string ModelDeployment { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<AssessmentStatus>))]
    public required AssessmentStatus AssessmentStatus { get; init; }
    public required decimal? OverallScore { get; init; }
    public required int ScoredCategoryCount { get; init; }
    public required int UsableAdvisorTurnCount { get; init; }
    public required bool IsSimulated { get; init; }
    public required CoachingReportContent Content { get; init; }

    /// <summary>Maps model turn labels (T1, T2, ...) to canonical transcript turn IDs.</summary>
    public required IReadOnlyDictionary<string, Guid> TurnLabels { get; init; }
}

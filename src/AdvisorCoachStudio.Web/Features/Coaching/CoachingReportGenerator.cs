using System.Text.Json;
using System.Text.Json.Serialization;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;

namespace AdvisorCoachStudio.Web.Features.Coaching;

public sealed class ReportGenerationException(string code, bool retryable) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Generates and validates a coaching report for a frozen transcript revision (spec section 11.4).
/// The model produces content only; identity, versions, timestamps, and scores are assigned here.
/// </summary>
public sealed class CoachingReportGenerator(
    IReportModelClient client,
    ReportSchema schema,
    ScenarioCatalog catalog,
    TimeProvider time,
    ILogger<CoachingReportGenerator> logger)
{
    public static readonly JsonSerializerOptions ContentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public async Task<CoachingReport> GenerateAsync(PracticeSession session, IReadOnlyList<TranscriptTurn> turns, int revision, string? transcriptHash, CancellationToken cancellationToken)
    {
        var usableAdvisorTurns = turns.Count(t => t.Speaker == TurnSpeaker.Advisor && t.IsQuotable);
        if (usableAdvisorTurns == 0)
        {
            return Build(session, revision, transcriptHash, InsufficientEvidenceContent(session.Scenario), usableAdvisorTurns, "none");
        }

        var (userMessage, turnsByLabel) = CoachingPromptBuilder.UserMessage(session.Scenario, session.Difficulty, turns);
        var messages = new List<ReportChatMessage>
        {
            new("system", CoachingPromptBuilder.SystemPrompt(catalog.All.Select(s => s.Id).ToList())),
            new("user", userMessage),
        };

        for (var pass = 0; pass < 2; pass++)
        {
            var result = await client.CompleteAsync(messages, schema.ProviderSchema, cancellationToken);
            var (content, problems) = Evaluate(result, turnsByLabel);
            if (content is not null)
            {
                return Build(session, revision, transcriptHash, content, usableAdvisorTurns, client.DeploymentName, turnsByLabel);
            }

            // Problems name fields and turn labels only, never transcript content, so they are safe to log.
            logger.LogWarning(
                "Report output for session {SessionId} failed validation on pass {Pass}: {ProblemCount} problem(s): {Problems}",
                session.Id, pass + 1, problems.Count, string.Join(" | ", problems.Take(15)));
            if (pass == 0 && problems.Count > 0 && result.Content is not null)
            {
                // One bounded correction request for schema/evidence errors.
                messages.Add(new ReportChatMessage("assistant", result.Content));
                messages.Add(new ReportChatMessage("user", CoachingPromptBuilder.CorrectionMessage(problems)));
                continue;
            }

            break;
        }

        throw new ReportGenerationException("invalid_report_output", retryable: true);
    }

    /// <summary>Checks finish status, refusal, JSON, schema, and grounding. Throws for non-correctable outcomes.</summary>
    private (CoachingReportContent? Content, IReadOnlyList<string> Problems) Evaluate(ReportModelResult result, IReadOnlyDictionary<string, TranscriptTurn> turnsByLabel)
    {
        if (!string.IsNullOrEmpty(result.Refusal))
        {
            throw new ReportGenerationException("report_refused", retryable: false);
        }

        switch (result.FinishReason)
        {
            case "content_filter":
                throw new ReportGenerationException("report_content_filtered", retryable: false);
            case "length":
                return (null, ["The output was truncated; keep every text field shorter."]);
        }

        if (string.IsNullOrWhiteSpace(result.Content))
        {
            throw new ReportGenerationException("report_empty_output", retryable: true);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Content);
        }
        catch (JsonException)
        {
            return (null, ["The output was not valid JSON."]);
        }

        using (document)
        {
            var schemaProblems = schema.Validate(document.RootElement);
            if (schemaProblems.Count > 0)
            {
                return (null, schemaProblems);
            }

            CoachingReportContent? content;
            try
            {
                content = document.RootElement.Deserialize<CoachingReportContent>(ContentJsonOptions);
            }
            catch (JsonException)
            {
                return (null, ["The output did not match the report structure."]);
            }

            if (content is null)
            {
                return (null, ["The output was empty."]);
            }

            content = QuoteCanonicalizer.Canonicalize(content, turnsByLabel);
            var problems = ReportValidator.Validate(content, turnsByLabel, catalog);
            return problems.Count == 0 ? (content, []) : (null, problems);
        }
    }

    private CoachingReport Build(PracticeSession session, int revision, string? hash, CoachingReportContent content, int usableAdvisorTurns, string deployment, IReadOnlyDictionary<string, TranscriptTurn>? turnsByLabel = null)
    {
        var (overall, scoredCount) = ScoreCalculator.Compute(content.SkillScores.Select(s => s.Score), usableAdvisorTurns);
        return new CoachingReport
        {
            SchemaVersion = Rubric.SchemaVersion,
            SessionId = session.Id,
            ScenarioId = session.Scenario.Id,
            ScenarioVersion = session.Scenario.Version,
            TranscriptRevision = revision,
            TranscriptHash = hash,
            GeneratedAtUtc = time.GetUtcNow().UtcDateTime,
            PromptVersion = CoachingPromptBuilder.Version,
            RubricVersion = Rubric.Version,
            ModelDeployment = deployment,
            AssessmentStatus = overall is null ? AssessmentStatus.LimitedEvidence : AssessmentStatus.SufficientEvidence,
            OverallScore = overall,
            ScoredCategoryCount = scoredCount,
            UsableAdvisorTurnCount = usableAdvisorTurns,
            IsSimulated = session.IsSimulated,
            Content = content,
            TurnLabels = (turnsByLabel ?? new Dictionary<string, TranscriptTurn>()).ToDictionary(pair => pair.Key, pair => pair.Value.Id),
        };
    }

    /// <summary>Honest outcome when no advisor speech was captured: no model call and no invented feedback.</summary>
    private static CoachingReportContent InsufficientEvidenceContent(ScenarioDefinition scenario) => new()
    {
        Summary = "There was not enough of your side of the conversation in the transcript to assess. This can happen when the session ends very early, the microphone was muted, or speech recognition could not capture your words.",
        KeyStrength = null,
        PriorityImprovement = null,
        SkillScores = Rubric.Categories.Select(c => new SkillScoreContent
        {
            CategoryId = c.Id,
            Score = null,
            Explanation = "Not enough evidence in the transcript to assess this skill.",
            Evidence = [],
        }).ToList(),
        Strengths = [],
        Improvements = [],
        AlternativePhrases = [],
        MissedDiscoveryQuestions = [],
        PracticePlan = new PracticePlanContent
        {
            FocusAreas = [],
            Exercise = "Start a new attempt and check that the microphone level meter moves while you speak. Aim for several back-and-forth exchanges before ending the session.",
            RecommendedScenarioId = scenario.Id,
        },
        Warnings = ["No usable advisor speech was transcribed, so no skills could be scored."],
    };
}

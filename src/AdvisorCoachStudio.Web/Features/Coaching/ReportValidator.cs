using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.Web.Features.Coaching;

/// <summary>
/// Validates bounded lengths, identifiers, and evidence grounding (spec section 11.3). Every quote must be a
/// non-empty, exact (ordinal) substring of the referenced canonical turn, and only quotable turns may be cited.
/// Error messages reference fields and turn labels only, never transcript content.
/// </summary>
public static class ReportValidator
{
    public const int MaxSummary = 1200;
    public const int MaxShortText = 160;
    public const int MaxText = 700;
    public const int MaxQuote = 400;
    public const int MaxEvidencePerItem = 4;
    public const int MaxStrengths = 4;
    public const int MaxImprovements = 4;
    public const int MaxAlternativePhrases = 3;
    public const int MaxMissedQuestions = 4;
    public const int MaxFocusAreas = 2;
    public const int MaxWarnings = 5;

    public static List<string> Validate(CoachingReportContent content, IReadOnlyDictionary<string, TranscriptTurn> turnsByLabel, ScenarioCatalog catalog)
    {
        var errors = new List<string>();

        Text(errors, "summary", content.Summary, MaxSummary, required: true);
        Text(errors, "keyStrength", content.KeyStrength, MaxText, required: false);
        Text(errors, "priorityImprovement", content.PriorityImprovement, MaxText, required: false);

        // Exactly six unique rubric categories.
        var categories = content.SkillScores.Select(s => s.CategoryId).ToList();
        if (content.SkillScores.Count != Rubric.Categories.Count || categories.Distinct(StringComparer.Ordinal).Count() != categories.Count ||
            Rubric.Categories.Any(c => !categories.Contains(c.Id)))
        {
            errors.Add("skillScores: must contain each of the six rubric categories exactly once.");
        }

        for (var i = 0; i < content.SkillScores.Count; i++)
        {
            var score = content.SkillScores[i];
            var path = $"skillScores[{i}]";
            if (!Rubric.IsCategory(score.CategoryId))
            {
                errors.Add($"{path}.categoryId: unknown category.");
            }

            if (score.Score is < 1 or > 5)
            {
                errors.Add($"{path}.score: must be an integer from 1 to 5, or null.");
            }

            Text(errors, $"{path}.explanation", score.Explanation, MaxText, required: true);
            Evidence(errors, path + ".evidence", score.Evidence, turnsByLabel, requireAtLeastOne: score.Score is not null, advisorOnly: false);
        }

        Count(errors, "strengths", content.Strengths.Count, MaxStrengths);
        for (var i = 0; i < content.Strengths.Count; i++)
        {
            var strength = content.Strengths[i];
            var path = $"strengths[{i}]";
            Text(errors, $"{path}.title", strength.Title, MaxShortText, required: true);
            Text(errors, $"{path}.observation", strength.Observation, MaxText, required: true);
            Text(errors, $"{path}.interpretation", strength.Interpretation, MaxText, required: false);
            Category(errors, $"{path}.categoryId", strength.CategoryId);
            Evidence(errors, path + ".evidence", strength.Evidence, turnsByLabel, requireAtLeastOne: true, advisorOnly: false);
        }

        Count(errors, "improvements", content.Improvements.Count, MaxImprovements);
        for (var i = 0; i < content.Improvements.Count; i++)
        {
            var improvement = content.Improvements[i];
            var path = $"improvements[{i}]";
            Text(errors, $"{path}.title", improvement.Title, MaxShortText, required: true);
            Text(errors, $"{path}.observation", improvement.Observation, MaxText, required: true);
            Text(errors, $"{path}.interpretation", improvement.Interpretation, MaxText, required: false);
            Text(errors, $"{path}.suggestedAdjustment", improvement.SuggestedAdjustment, MaxText, required: true);
            Category(errors, $"{path}.categoryId", improvement.CategoryId);
            Evidence(errors, path + ".evidence", improvement.Evidence, turnsByLabel, requireAtLeastOne: true, advisorOnly: false);
        }

        Count(errors, "alternativePhrases", content.AlternativePhrases.Count, MaxAlternativePhrases);
        for (var i = 0; i < content.AlternativePhrases.Count; i++)
        {
            var phrase = content.AlternativePhrases[i];
            var path = $"alternativePhrases[{i}]";
            Evidence(errors, path + ".originalEvidence", [phrase.OriginalEvidence], turnsByLabel, requireAtLeastOne: true, advisorOnly: true);
            Text(errors, $"{path}.suggestedPhrase", phrase.SuggestedPhrase, MaxText, required: true);
            Text(errors, $"{path}.communicationIntention", phrase.CommunicationIntention, MaxText, required: true);
            Text(errors, $"{path}.explanation", phrase.Explanation, MaxText, required: true);
        }

        Count(errors, "missedDiscoveryQuestions", content.MissedDiscoveryQuestions.Count, MaxMissedQuestions);
        for (var i = 0; i < content.MissedDiscoveryQuestions.Count; i++)
        {
            var question = content.MissedDiscoveryQuestions[i];
            var path = $"missedDiscoveryQuestions[{i}]";
            Text(errors, $"{path}.question", question.Question, MaxText, required: true);
            Text(errors, $"{path}.purpose", question.Purpose, MaxText, required: true);
            Text(errors, $"{path}.relevance", question.Relevance, MaxText, required: true);
            Count(errors, $"{path}.relatedTurnIds", question.RelatedTurnIds.Count, MaxEvidencePerItem);
            foreach (var turnId in question.RelatedTurnIds)
            {
                if (!turnsByLabel.ContainsKey(turnId))
                {
                    errors.Add($"{path}.relatedTurnIds: unknown turn id '{Safe(turnId)}'.");
                }
            }
        }

        var plan = content.PracticePlan;
        Count(errors, "practicePlan.focusAreas", plan.FocusAreas.Count, MaxFocusAreas);
        for (var i = 0; i < plan.FocusAreas.Count; i++)
        {
            Text(errors, $"practicePlan.focusAreas[{i}]", plan.FocusAreas[i], MaxShortText, required: true);
        }

        Text(errors, "practicePlan.exercise", plan.Exercise, MaxText, required: true);
        if (!catalog.Contains(plan.RecommendedScenarioId))
        {
            errors.Add("practicePlan.recommendedScenarioId: unsupported scenario.");
        }

        Count(errors, "warnings", content.Warnings.Count, MaxWarnings);
        for (var i = 0; i < content.Warnings.Count; i++)
        {
            Text(errors, $"warnings[{i}]", content.Warnings[i], MaxText, required: true);
        }

        return errors;
    }

    private static void Evidence(List<string> errors, string path, IReadOnlyList<ReportEvidence> evidence, IReadOnlyDictionary<string, TranscriptTurn> turnsByLabel, bool requireAtLeastOne, bool advisorOnly)
    {
        if (requireAtLeastOne && evidence.Count == 0)
        {
            errors.Add($"{path}: at least one transcript reference is required.");
        }

        Count(errors, path, evidence.Count, MaxEvidencePerItem);
        for (var i = 0; i < evidence.Count; i++)
        {
            var item = evidence[i];
            var itemPath = evidence.Count == 1 && path.EndsWith("originalEvidence", StringComparison.Ordinal) ? path : $"{path}[{i}]";
            if (!turnsByLabel.TryGetValue(item.TurnId, out var turn))
            {
                errors.Add($"{itemPath}.turnId: unknown turn id '{Safe(item.TurnId)}'.");
                continue;
            }

            if (!turn.IsQuotable)
            {
                errors.Add($"{itemPath}.turnId: turn {item.TurnId} is not quotable (pending, failed, or not fully heard).");
                continue;
            }

            if (advisorOnly && turn.Speaker != TurnSpeaker.Advisor)
            {
                errors.Add($"{itemPath}.turnId: turn {item.TurnId} is not an advisor turn.");
            }

            if (string.IsNullOrWhiteSpace(item.Quote))
            {
                errors.Add($"{itemPath}.quote: must not be empty.");
            }
            else if (item.Quote.Length > MaxQuote)
            {
                errors.Add($"{itemPath}.quote: too long.");
            }
            else if (!turn.Text.Contains(item.Quote, StringComparison.Ordinal))
            {
                errors.Add($"{itemPath}.quote: is not an exact substring of turn {item.TurnId}.");
            }

            Text(errors, $"{itemPath}.explanation", item.Explanation, MaxText, required: true);
        }
    }

    private static void Category(List<string> errors, string path, string id)
    {
        if (!Rubric.IsCategory(id))
        {
            errors.Add($"{path}: unknown category.");
        }
    }

    private static void Text(List<string> errors, string path, string? value, int max, bool required)
    {
        if (value is null)
        {
            if (required)
            {
                errors.Add($"{path}: is required.");
            }

            return;
        }

        if (required && string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path}: must not be empty.");
        }
        else if (value.Length > max)
        {
            errors.Add($"{path}: exceeds {max} characters.");
        }
    }

    private static void Count(List<string> errors, string path, int count, int max)
    {
        if (count > max)
        {
            errors.Add($"{path}: at most {max} entries are allowed.");
        }
    }

    private static string Safe(string value) => value.Length <= 12 ? value : value[..12] + "...";
}

/// <summary>Server-side overall score (spec section 11.1). The model never computes it.</summary>
public static class ScoreCalculator
{
    public const int MinimumScoredCategories = 3;
    public const int MinimumAdvisorTurns = 3;

    /// <summary>
    /// Equal-weight mean of non-null category scores, rounded once to one decimal place using
    /// MidpointRounding.AwayFromZero (e.g. 3.25 -> 3.3). Null when fewer than three categories are scored
    /// or fewer than three usable final advisor turns exist. Missing evidence never counts as zero.
    /// </summary>
    public static (decimal? Overall, int ScoredCount) Compute(IEnumerable<int?> scores, int usableAdvisorTurns)
    {
        var scored = scores.Where(s => s is not null).Select(s => s!.Value).ToList();
        if (scored.Count < MinimumScoredCategories || usableAdvisorTurns < MinimumAdvisorTurns)
        {
            return (null, scored.Count);
        }

        var mean = (decimal)scored.Sum() / scored.Count;
        return (Math.Round(mean, 1, MidpointRounding.AwayFromZero), scored.Count);
    }
}

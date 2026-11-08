using System.Text.RegularExpressions;

namespace AdvisorCoachStudio.Web.Features.Scenarios;

/// <summary>Validates scenario definitions (spec section 5.1 "Required validation").</summary>
public static partial class ScenarioValidator
{
    public static List<string> Validate(ScenarioDefinition scenario)
    {
        var errors = new List<string>();
        var prefix = string.IsNullOrWhiteSpace(scenario.Id) ? "(missing id)" : scenario.Id;

        void Require(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{prefix}: '{field}' must not be empty.");
            }
        }

        void RequireList(IReadOnlyList<string>? values, string field, bool allowEmpty = false)
        {
            if (values is null || (!allowEmpty && values.Count == 0))
            {
                errors.Add($"{prefix}: '{field}' must contain at least one entry.");
                return;
            }

            for (var i = 0; i < values.Count; i++)
            {
                Require(values[i], $"{field}[{i}]");
            }
        }

        if (string.IsNullOrWhiteSpace(scenario.Id) || !IdPattern().IsMatch(scenario.Id) || scenario.Id.Length > 64)
        {
            errors.Add($"{prefix}: 'id' must be a stable lowercase slug (a-z, 0-9, hyphen), at most 64 characters.");
        }

        if (string.IsNullOrWhiteSpace(scenario.Version) || !VersionPattern().IsMatch(scenario.Version))
        {
            errors.Add($"{prefix}: 'version' must be an explicit semantic version such as 1.0.0.");
        }

        Require(scenario.Title, "title");
        Require(scenario.PublicBrief, "publicBrief");
        Require(scenario.ClientDisplayName, "clientDisplayName");
        Require(scenario.ClientProfile, "clientProfile");
        Require(scenario.InitialEmotionalState, "initialEmotionalState");
        Require(scenario.OpeningUtterance, "openingUtterance");
        RequireList(scenario.SkillFocus, "skillFocus");
        RequireList(scenario.KnownFacts, "knownFacts");
        RequireList(scenario.Goals, "goals");
        RequireList(scenario.Concerns, "concerns");
        RequireList(scenario.BehaviorGuidance, "behaviorGuidance");
        RequireList(scenario.SafetyBoundaries, "safetyBoundaries");
        RequireList(scenario.CoachingFocus, "coachingFocus");

        if (scenario.DifficultyGuidance is null)
        {
            errors.Add($"{prefix}: 'difficultyGuidance' must define beginner, intermediate, and advanced.");
        }
        else
        {
            Require(scenario.DifficultyGuidance.Beginner, "difficultyGuidance.beginner");
            Require(scenario.DifficultyGuidance.Intermediate, "difficultyGuidance.intermediate");
            Require(scenario.DifficultyGuidance.Advanced, "difficultyGuidance.advanced");
        }

        if (scenario.HiddenFacts is null || scenario.HiddenFacts.Count == 0)
        {
            errors.Add($"{prefix}: 'hiddenFacts' must contain at least one entry.");
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fact in scenario.HiddenFacts)
            {
                Require(fact.Id, "hiddenFacts.id");
                Require(fact.Fact, $"hiddenFacts[{fact.Id}].fact");
                Require(fact.DiscloseWhen, $"hiddenFacts[{fact.Id}].discloseWhen");
                if (!string.IsNullOrWhiteSpace(fact.Id) && !seen.Add(fact.Id))
                {
                    errors.Add($"{prefix}: hidden fact id '{fact.Id}' is duplicated.");
                }
            }
        }

        foreach (var (field, text) in AllText(scenario))
        {
            if (LooksLikeRealIdentifier(text))
            {
                errors.Add($"{prefix}: '{field}' appears to contain a real identifier or unestablished financial specific (email, URL, phone/account number, amount, or percentage).");
            }
        }

        return errors;
    }

    /// <summary>Fictional scenarios must not contain identifiers or invented exact figures.</summary>
    public static bool LooksLikeRealIdentifier(string text) =>
        EmailPattern().IsMatch(text) || UrlPattern().IsMatch(text) || LongDigitsPattern().IsMatch(text) ||
        AmountPattern().IsMatch(text) || PercentPattern().IsMatch(text);

    private static IEnumerable<(string Field, string Text)> AllText(ScenarioDefinition s)
    {
        yield return ("title", s.Title ?? "");
        yield return ("publicBrief", s.PublicBrief ?? "");
        yield return ("clientDisplayName", s.ClientDisplayName ?? "");
        yield return ("clientProfile", s.ClientProfile ?? "");
        yield return ("initialEmotionalState", s.InitialEmotionalState ?? "");
        yield return ("openingUtterance", s.OpeningUtterance ?? "");
        foreach (var list in new[]
        {
            ("skillFocus", s.SkillFocus), ("knownFacts", s.KnownFacts), ("goals", s.Goals), ("concerns", s.Concerns),
            ("behaviorGuidance", s.BehaviorGuidance), ("safetyBoundaries", s.SafetyBoundaries), ("coachingFocus", s.CoachingFocus),
        })
        {
            foreach (var item in list.Item2 ?? [])
            {
                yield return (list.Item1, item ?? "");
            }
        }

        if (s.DifficultyGuidance is { } guidance)
        {
            yield return ("difficultyGuidance.beginner", guidance.Beginner ?? "");
            yield return ("difficultyGuidance.intermediate", guidance.Intermediate ?? "");
            yield return ("difficultyGuidance.advanced", guidance.Advanced ?? "");
        }

        foreach (var fact in s.HiddenFacts ?? [])
        {
            yield return ($"hiddenFacts[{fact.Id}]", $"{fact.Fact} {fact.DiscloseWhen}");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(https?://|www\.)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\d[\d\s-]{4,}\d")]
    private static partial Regex LongDigitsPattern();

    [GeneratedRegex(@"[$€£¥]\s?\d")]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"\d+(\.\d+)?\s?(%|percent)", RegexOptions.IgnoreCase)]
    private static partial Regex PercentPattern();
}

using System.Text.Json.Serialization;
using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.Web.Features.Scenarios;

/// <summary>Private, versioned scenario definition (spec section 5.1). Never serialized to browsers.</summary>
public sealed record ScenarioDefinition
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Title { get; init; }
    public required string PublicBrief { get; init; }
    public required IReadOnlyList<string> SkillFocus { get; init; }
    public required string ClientDisplayName { get; init; }
    public required string ClientProfile { get; init; }
    public required IReadOnlyList<string> KnownFacts { get; init; }
    public required IReadOnlyList<HiddenFact> HiddenFacts { get; init; }
    public required IReadOnlyList<string> Goals { get; init; }
    public required IReadOnlyList<string> Concerns { get; init; }
    public required string InitialEmotionalState { get; init; }
    public required string OpeningUtterance { get; init; }
    public required IReadOnlyList<string> BehaviorGuidance { get; init; }
    public required DifficultyGuidance DifficultyGuidance { get; init; }
    public required IReadOnlyList<string> SafetyBoundaries { get; init; }
    public required IReadOnlyList<string> CoachingFocus { get; init; }

    [JsonIgnore]
    public string ClientInitials => string.Concat(
        ClientDisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => char.ToUpperInvariant(part[0])));

    public string GuidanceFor(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Beginner => DifficultyGuidance.Beginner,
        Difficulty.Intermediate => DifficultyGuidance.Intermediate,
        Difficulty.Advanced => DifficultyGuidance.Advanced,
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
    };
}

public sealed record HiddenFact
{
    public required string Id { get; init; }
    public required string Fact { get; init; }
    public required string DiscloseWhen { get; init; }
}

public sealed record DifficultyGuidance
{
    public required string Beginner { get; init; }
    public required string Intermediate { get; init; }
    public required string Advanced { get; init; }
}

/// <summary>Public difficulty descriptions (spec section 5.5). Safe to show in the browser.</summary>
public static class DifficultyInfo
{
    public static readonly IReadOnlyList<(Difficulty Level, string Name, string Description)> All =
    [
        (Difficulty.Beginner, "Beginner", "Cooperative client who shares relevant facts after broad questions, giving you time to practise fundamentals."),
        (Difficulty.Intermediate, "Intermediate", "Shares facts when appropriately prompted, asks realistic follow-up questions, and challenges incomplete answers."),
        (Difficulty.Advanced, "Advanced", "More guarded and emotionally nuanced, raises competing concerns, and needs stronger conversation management."),
    ];

    public static string NameOf(Difficulty difficulty) => All.First(item => item.Level == difficulty).Name;

    public static bool TryParse(string? value, out Difficulty difficulty)
    {
        foreach (var item in All)
        {
            if (string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase))
            {
                difficulty = item.Level;
                return true;
            }
        }

        difficulty = Difficulty.Intermediate;
        return false;
    }
}

using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>Synthetic scenario builder plus the real, checked-in scenario catalogue.</summary>
internal static class TestScenarios
{
    public const string MarketDecline = "market-decline";
    public const string FeesAndTrust = "fees-and-trust";
    public const string FirstTimeInvestor = "first-time-investor";

    private static readonly Lazy<ScenarioCatalog> LazyCatalog = new(() => ScenarioCatalog.LoadFromDirectory(RepoPaths.ScenariosDirectory));

    /// <summary>Documented catalogue order (spec sections 5.2-5.4).</summary>
    public static IReadOnlyList<string> ShippedIds { get; } = [MarketDecline, FeesAndTrust, FirstTimeInvestor];

    public static ScenarioCatalog Catalog => LazyCatalog.Value;

    public static ScenarioDefinition Shipped(string id) =>
        Catalog.TryGet(id, out var scenario) ? scenario : throw new InvalidOperationException($"Scenario '{id}' is not in the catalogue.");

    /// <summary>A fully valid, fictional scenario with no identifiers, figures, or real names.</summary>
    public static ScenarioDefinition Valid(string id = "synthetic-scenario") => new()
    {
        Id = id,
        Version = "1.0.0",
        Title = "Synthetic client reviewing a savings plan",
        PublicBrief = "Sam wants to talk through a savings plan before making any decision.",
        SkillFocus = ["Discovery", "Plain language"],
        ClientDisplayName = "Sam Rivers",
        ClientProfile = "Sam Rivers is a fictional client who likes to understand options before acting.",
        KnownFacts = ["Sam has some savings.", "Sam wants to understand the available options."],
        HiddenFacts =
        [
            new HiddenFact
            {
                Id = "prefers-time",
                Fact = "Sam prefers to take time before deciding.",
                DiscloseWhen = "The advisor asks how Sam likes to make decisions.",
            },
            new HiddenFact
            {
                Id = "family-goal",
                Fact = "Sam is saving towards a family goal.",
                DiscloseWhen = "The advisor asks about goals or priorities.",
            },
        ],
        Goals = ["Understand the options in plain language."],
        Concerns = ["Making a rushed decision."],
        InitialEmotionalState = "Curious but cautious.",
        OpeningUtterance = "I'd like to understand my options before I decide anything.",
        BehaviorGuidance = ["Be polite and curious.", "Never invent exact figures."],
        DifficultyGuidance = new DifficultyGuidance
        {
            Beginner = "Synthetic beginner guidance: be very cooperative.",
            Intermediate = "Synthetic intermediate guidance: share details when prompted.",
            Advanced = "Synthetic advanced guidance: stay guarded until trust is built.",
        },
        SafetyBoundaries = ["Do not ask for or offer real personal information."],
        CoachingFocus = ["Discovery of goals", "Plain language"],
    };
}

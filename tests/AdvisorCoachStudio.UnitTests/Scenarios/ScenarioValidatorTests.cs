using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.UnitTests.Scenarios;

public sealed class ScenarioValidatorTests
{
    private const string IdentifierError = "appears to contain a real identifier";

    public static TheoryData<string> InvalidIds => new()
    {
        "Market-Decline", "market_decline", "market decline", "-market", "market-", "market--decline", "", " ", new string('a', 65),
    };

    public static TheoryData<string> ScannedFields => new()
    {
        "title", "publicBrief", "clientDisplayName", "clientProfile", "initialEmotionalState", "openingUtterance",
        "knownFacts", "goals", "concerns", "behaviorGuidance", "hiddenFacts.fact", "hiddenFacts.discloseWhen",
    };

    [Fact]
    public void SyntheticScenarioIsValid() => Assert.Empty(ScenarioValidator.Validate(TestScenarios.Valid()));

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public void RejectsIdThatIsNotAStableLowercaseSlug(string id)
    {
        var errors = ScenarioValidator.Validate(TestScenarios.Valid() with { Id = id });

        Assert.Contains(errors, error => error.Contains("'id' must be a stable lowercase slug", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsSixtyFourCharacterId() => Assert.Empty(ScenarioValidator.Validate(TestScenarios.Valid(new string('a', 64))));

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0.0")]
    [InlineData("latest")]
    [InlineData("")]
    public void RejectsVersionThatIsNotExplicitSemanticVersion(string version)
    {
        var errors = ScenarioValidator.Validate(TestScenarios.Valid() with { Version = version });

        Assert.Equal("synthetic-scenario: 'version' must be an explicit semantic version such as 1.0.0.", Assert.Single(errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsEmptyOpeningUtteranceAndPublicBrief(string blank)
    {
        var errors = ScenarioValidator.Validate(TestScenarios.Valid() with { OpeningUtterance = blank, PublicBrief = blank });

        Assert.Contains("synthetic-scenario: 'openingUtterance' must not be empty.", errors);
        Assert.Contains("synthetic-scenario: 'publicBrief' must not be empty.", errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RejectsHiddenFactWithoutDisclosureCondition(string condition)
    {
        var scenario = TestScenarios.Valid();
        var errors = ScenarioValidator.Validate(scenario with { HiddenFacts = [scenario.HiddenFacts[0], scenario.HiddenFacts[1] with { DiscloseWhen = condition }] });

        Assert.Equal("synthetic-scenario: 'hiddenFacts[family-goal].discloseWhen' must not be empty.", Assert.Single(errors));
    }

    [Fact]
    public void RejectsDuplicateHiddenFactIds()
    {
        var scenario = TestScenarios.Valid();
        var duplicate = scenario.HiddenFacts[0] with { Fact = "Sam also prefers written summaries." };

        var errors = ScenarioValidator.Validate(scenario with { HiddenFacts = [.. scenario.HiddenFacts, duplicate] });

        Assert.Equal("synthetic-scenario: hidden fact id 'prefers-time' is duplicated.", Assert.Single(errors));
    }

    [Fact]
    public void RejectsScenarioWithoutHiddenFacts()
    {
        var errors = ScenarioValidator.Validate(TestScenarios.Valid() with { HiddenFacts = [] });

        Assert.Equal("synthetic-scenario: 'hiddenFacts' must contain at least one entry.", Assert.Single(errors));
    }

    [Fact]
    public void RejectsMissingDifficultyGuidance()
    {
        var errors = ScenarioValidator.Validate(TestScenarios.Valid() with { DifficultyGuidance = null! });

        Assert.Equal("synthetic-scenario: 'difficultyGuidance' must define beginner, intermediate, and advanced.", Assert.Single(errors));
    }

    [Theory]
    [InlineData("beginner")]
    [InlineData("intermediate")]
    [InlineData("advanced")]
    public void RejectsEmptyDifficultyLevel(string level)
    {
        var guidance = TestScenarios.Valid().DifficultyGuidance;
        guidance = level switch
        {
            "beginner" => guidance with { Beginner = " " },
            "intermediate" => guidance with { Intermediate = "" },
            _ => guidance with { Advanced = "\t" },
        };

        var errors = ScenarioValidator.Validate(TestScenarios.Valid() with { DifficultyGuidance = guidance });

        Assert.Equal($"synthetic-scenario: 'difficultyGuidance.{level}' must not be empty.", Assert.Single(errors));
    }

    [Fact]
    public void RejectsEmptyListsAndBlankListEntries()
    {
        var errors = ScenarioValidator.Validate(TestScenarios.Valid() with { KnownFacts = [], SafetyBoundaries = ["A valid boundary.", "  "] });

        Assert.Contains("synthetic-scenario: 'knownFacts' must contain at least one entry.", errors);
        Assert.Contains("synthetic-scenario: 'safetyBoundaries[1]' must not be empty.", errors);
    }

    [Theory]
    [InlineData("Write to sam.rivers@example.com for details.")]
    [InlineData("See https://example.org/savings for the plan.")]
    [InlineData("See http://example.org for the plan.")]
    [InlineData("Visit www.example.org before the meeting.")]
    [InlineData("The account number is 12345678.")]
    [InlineData("Call 555 0100 2233 after lunch.")]
    [InlineData("Quote reference 0042-7731-90 on the form.")]
    [InlineData("The balance fell by $25,000 this month.")]
    [InlineData("Fees are about £300 a year.")]
    [InlineData("The fund charges € 12 monthly.")]
    [InlineData("The portfolio is down 12% this year.")]
    [InlineData("Fees are 1.5 % of the balance.")]
    [InlineData("Returns were around 7 percent.")]
    public void RejectsIdentifiersAndInventedFigures(string text)
    {
        var scenario = TestScenarios.Valid();

        var errors = ScenarioValidator.Validate(scenario with { KnownFacts = [.. scenario.KnownFacts, text] });

        Assert.True(ScenarioValidator.LooksLikeRealIdentifier(text));
        Assert.Contains(errors, error => error.StartsWith("synthetic-scenario: 'knownFacts' " + IdentifierError, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Alex expects to retire in approximately five years.")]
    [InlineData("Retire in 5 years.")]
    [InlineData("A two-step plan with 3 options.")]
    [InlineData("Sam is 45 and works full time.")]
    public void AllowsOrdinaryFictionalText(string text)
    {
        var scenario = TestScenarios.Valid();

        Assert.False(ScenarioValidator.LooksLikeRealIdentifier(text));
        Assert.Empty(ScenarioValidator.Validate(scenario with { KnownFacts = [.. scenario.KnownFacts, text] }));
    }

    [Theory]
    [MemberData(nameof(ScannedFields))]
    public void RejectsIdentifiersInEveryScannedField(string field)
    {
        var errors = ScenarioValidator.Validate(WithText(TestScenarios.Valid(), field, "Contact sam.rivers@example.com"));

        Assert.Contains(errors, error => error.Contains(IdentifierError, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("skillFocus")]
    [InlineData("difficultyGuidance")]
    [InlineData("safetyBoundaries")]
    [InlineData("coachingFocus")]
    public void RejectsIdentifiersInPromptAndPublicLists(string field)
    {
        var errors = ScenarioValidator.Validate(WithText(TestScenarios.Valid(), field, "Say the portfolio fell 20% to $40,000"));

        Assert.Contains(errors, error => error.Contains(IdentifierError, StringComparison.Ordinal));
    }

    private static ScenarioDefinition WithText(ScenarioDefinition s, string field, string text) => field switch
    {
        "title" => s with { Title = text },
        "publicBrief" => s with { PublicBrief = text },
        "clientDisplayName" => s with { ClientDisplayName = text },
        "clientProfile" => s with { ClientProfile = text },
        "initialEmotionalState" => s with { InitialEmotionalState = text },
        "openingUtterance" => s with { OpeningUtterance = text },
        "knownFacts" => s with { KnownFacts = [.. s.KnownFacts, text] },
        "goals" => s with { Goals = [.. s.Goals, text] },
        "concerns" => s with { Concerns = [.. s.Concerns, text] },
        "behaviorGuidance" => s with { BehaviorGuidance = [.. s.BehaviorGuidance, text] },
        "hiddenFacts.fact" => s with { HiddenFacts = [s.HiddenFacts[0] with { Fact = text }, s.HiddenFacts[1]] },
        "hiddenFacts.discloseWhen" => s with { HiddenFacts = [s.HiddenFacts[0], s.HiddenFacts[1] with { DiscloseWhen = text }] },
        "skillFocus" => s with { SkillFocus = [.. s.SkillFocus, text] },
        "difficultyGuidance" => s with { DifficultyGuidance = s.DifficultyGuidance with { Advanced = text } },
        "safetyBoundaries" => s with { SafetyBoundaries = [.. s.SafetyBoundaries, text] },
        "coachingFocus" => s with { CoachingFocus = [.. s.CoachingFocus, text] },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown scenario field."),
    };
}

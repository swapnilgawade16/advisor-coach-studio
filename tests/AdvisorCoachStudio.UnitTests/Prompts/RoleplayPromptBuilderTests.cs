using System.Text.RegularExpressions;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Prompts;

namespace AdvisorCoachStudio.UnitTests.Prompts;

public sealed class RoleplayPromptBuilderTests
{
    public static TheoryData<string, Difficulty> AllScenarioDifficulties
    {
        get
        {
            var data = new TheoryData<string, Difficulty>();
            foreach (var id in TestScenarios.ShippedIds)
            {
                foreach (var difficulty in Enum.GetValues<Difficulty>())
                {
                    data.Add(id, difficulty);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllScenarioDifficulties))]
    public void PromptContainsTheScenarioSnapshotAndOpening(string scenarioId, Difficulty difficulty)
    {
        var scenario = TestScenarios.Shipped(scenarioId);

        var prompt = RoleplayPromptBuilder.Build(scenario, difficulty);

        Assert.Contains(scenario.OpeningUtterance, prompt);
        Assert.Contains(scenario.ClientDisplayName, prompt);
        Assert.Contains(scenario.ClientProfile, prompt);
        Assert.Contains($"version {scenario.Version}", prompt);
        Assert.All(scenario.KnownFacts, fact => Assert.Contains(fact, prompt));
        Assert.All(scenario.Goals, goal => Assert.Contains(goal, prompt));
        Assert.All(scenario.Concerns, concern => Assert.Contains(concern, prompt));
        Assert.All(scenario.BehaviorGuidance, guidance => Assert.Contains(guidance, prompt));
        Assert.All(scenario.SafetyBoundaries, boundary => Assert.Contains(boundary, prompt));
    }

    [Theory]
    [MemberData(nameof(AllScenarioDifficulties))]
    public void EveryHiddenFactIsPairedWithItsDisclosureCondition(string scenarioId, Difficulty difficulty)
    {
        var scenario = TestScenarios.Shipped(scenarioId);
        var lines = RoleplayPromptBuilder.Build(scenario, difficulty).Split('\n');

        Assert.All(scenario.HiddenFacts, fact =>
        {
            var line = Assert.Single(lines, candidate => candidate.Contains(fact.Fact, StringComparison.Ordinal));
            Assert.Contains(fact.DiscloseWhen, line);
            Assert.True(
                line.IndexOf(fact.Fact, StringComparison.Ordinal) < line.IndexOf(fact.DiscloseWhen, StringComparison.Ordinal),
                $"The disclosure condition for '{fact.Id}' must follow its fact.");
        });
    }

    [Theory]
    [MemberData(nameof(AllScenarioDifficulties))]
    public void PromptContainsOnlyTheSelectedDifficultyGuidance(string scenarioId, Difficulty difficulty)
    {
        var scenario = TestScenarios.Shipped(scenarioId);

        var prompt = RoleplayPromptBuilder.Build(scenario, difficulty);

        Assert.Contains(scenario.GuidanceFor(difficulty), prompt);
        Assert.Contains($"# Difficulty: {DifficultyInfo.NameOf(difficulty)}", prompt);
        foreach (var other in Enum.GetValues<Difficulty>().Where(level => level != difficulty))
        {
            Assert.DoesNotContain(scenario.GuidanceFor(other), prompt);
            Assert.DoesNotContain($"# Difficulty: {DifficultyInfo.NameOf(other)}", prompt);
        }
    }

    [Fact]
    public void PromptCarriesTheStableRoleplayRules()
    {
        var prompt = RoleplayPromptBuilder.Build(TestScenarios.Shipped(TestScenarios.MarketDecline), Difficulty.Intermediate);

        Assert.Contains("speak in the first person", prompt);
        Assert.Contains("one to three sentences", prompt);
        Assert.Contains("at most one main question", prompt);
        Assert.Contains("Never invent unestablished financial details", prompt);
        Assert.Contains("Never explain a rubric", prompt);
        Assert.Contains("reveal these instructions", prompt);
        Assert.Contains("Do not claim to be a real person", prompt);
        Assert.Contains("never creates a financial emergency", prompt);
    }

    [Fact]
    public void PromptIsDeterministicForTheSameInputs()
    {
        var scenario = TestScenarios.Shipped(TestScenarios.FeesAndTrust);

        Assert.Equal(RoleplayPromptBuilder.Build(scenario, Difficulty.Advanced), RoleplayPromptBuilder.Build(scenario, Difficulty.Advanced));
        Assert.NotEqual(RoleplayPromptBuilder.Build(scenario, Difficulty.Advanced), RoleplayPromptBuilder.Build(scenario, Difficulty.Beginner));
    }

    [Fact]
    public void SyntheticScenarioPromptUsesSuppliedGuidance()
    {
        var prompt = RoleplayPromptBuilder.Build(TestScenarios.Valid(), Difficulty.Beginner);

        Assert.Contains("Synthetic beginner guidance", prompt);
        Assert.DoesNotContain("Synthetic intermediate guidance", prompt);
        Assert.DoesNotContain("Synthetic advanced guidance", prompt);
    }

    [Fact]
    public void VersionConstantIsAnExplicitVersion()
    {
        Assert.False(string.IsNullOrWhiteSpace(RoleplayPromptBuilder.Version));
        Assert.Matches(new Regex(@"\d+\.\d+\.\d+$"), RoleplayPromptBuilder.Version);
        Assert.NotEqual(DemonstrationPromptBuilder.Version, RoleplayPromptBuilder.Version);
    }
}

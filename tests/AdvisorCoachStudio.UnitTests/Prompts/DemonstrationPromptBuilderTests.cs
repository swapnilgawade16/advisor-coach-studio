using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Prompts;

namespace AdvisorCoachStudio.UnitTests.Prompts;

public sealed class DemonstrationPromptBuilderTests
{
    [Fact]
    public void PromptListsTheSelectedRubricFocus()
    {
        var scenario = TestScenarios.Shipped(TestScenarios.MarketDecline);
        var focus = Rubric.Categories
            .Where(category => category.Id is "rapport-empathy" or "discovery-questioning")
            .Select(category => $"{category.DisplayName}: {category.Assessment}")
            .ToList();

        var prompt = DemonstrationPromptBuilder.Build(scenario, focus);

        Assert.Equal(2, focus.Count);
        Assert.Contains("- Rapport and empathy: Acknowledges emotion and creates trust", prompt);
        Assert.Contains("- Discovery and questioning: Asks relevant, open questions and explores underlying needs", prompt);
        Assert.All(scenario.CoachingFocus, item => Assert.DoesNotContain($"- {item}", prompt));
        Assert.Contains(scenario.OpeningUtterance, prompt);
        Assert.Contains(scenario.ClientDisplayName, prompt);
    }

    [Fact]
    public void PromptFallsBackToScenarioCoachingFocusWhenNoFeedbackIsSelected()
    {
        var scenario = TestScenarios.Shipped(TestScenarios.FirstTimeInvestor);

        var prompt = DemonstrationPromptBuilder.Build(scenario, []);

        Assert.All(scenario.CoachingFocus, item => Assert.Contains($"- {item}", prompt));
    }

    [Fact]
    public void PromptForbidsRecommendationsPredictionsAndInventedDetails()
    {
        var prompt = DemonstrationPromptBuilder.Build(TestScenarios.Shipped(TestScenarios.FeesAndTrust), ["Plain language"]);

        Assert.Contains("Do not recommend specific investments, products, or actions as the right answer", prompt);
        Assert.Contains("do not predict markets or guarantee outcomes", prompt);
        Assert.Contains("Do not invent balances, fees, percentages, firm names", prompt);
        Assert.Contains("Do not mention scores, rubrics, or the learner's performance", prompt);
    }

    [Fact]
    public void PromptDoesNotExposeHiddenScenarioFacts()
    {
        var scenario = TestScenarios.Shipped(TestScenarios.MarketDecline);

        var prompt = DemonstrationPromptBuilder.Build(scenario, ["Empathy"]);

        Assert.All(scenario.HiddenFacts, fact => Assert.DoesNotContain(fact.Fact, prompt));
    }

    [Fact]
    public void VersionConstantIsNonEmpty() => Assert.False(string.IsNullOrWhiteSpace(DemonstrationPromptBuilder.Version));
}

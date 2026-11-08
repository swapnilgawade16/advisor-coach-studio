using System.Text;
using System.Text.Json;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.UnitTests.Scenarios;

/// <summary>The browser-facing projection must never carry hidden facts, triggers, prompt material, or the opening line.</summary>
public sealed class ScenarioPublicProjectionTests
{
    public static TheoryData<string> ShippedScenarioIds => new() { TestScenarios.MarketDecline, TestScenarios.FeesAndTrust, TestScenarios.FirstTimeInvestor };

    [Theory]
    [MemberData(nameof(ShippedScenarioIds))]
    public void SerializedProjectionContainsNoPrivateScenarioContent(string id)
    {
        var scenario = TestScenarios.Shipped(id);

        var exposed = DecodedText(Serialize(scenario));

        foreach (var fact in scenario.HiddenFacts)
        {
            Assert.DoesNotContain(fact.Fact, exposed);
            Assert.DoesNotContain(fact.DiscloseWhen, exposed);
            Assert.DoesNotContain(fact.Id, exposed);
        }

        Assert.DoesNotContain(scenario.ClientProfile, exposed);
        Assert.DoesNotContain(scenario.OpeningUtterance, exposed);
        Assert.DoesNotContain(scenario.InitialEmotionalState, exposed);
        Assert.All(scenario.BehaviorGuidance, guidance => Assert.DoesNotContain(guidance, exposed));
        Assert.All(scenario.KnownFacts, fact => Assert.DoesNotContain(fact, exposed));
        Assert.All(scenario.SafetyBoundaries, boundary => Assert.DoesNotContain(boundary, exposed));
        Assert.DoesNotContain(scenario.DifficultyGuidance.Beginner, exposed);
        Assert.DoesNotContain(scenario.DifficultyGuidance.Intermediate, exposed);
        Assert.DoesNotContain(scenario.DifficultyGuidance.Advanced, exposed);
    }

    [Theory]
    [MemberData(nameof(ShippedScenarioIds))]
    public void SerializedProjectionHasOnlyThePublicFields(string id)
    {
        var scenario = TestScenarios.Shipped(id);

        using var document = JsonDocument.Parse(Serialize(scenario));
        var root = document.RootElement;

        string[] expected = ["clientDisplayName", "clientInitials", "defaultDifficulty", "difficulties", "id", "publicBrief", "skillFocus", "suggestedDuration", "title", "version"];
        Assert.Equal(expected, root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(scenario.Id, root.GetProperty("id").GetString());
        Assert.Equal(scenario.PublicBrief, root.GetProperty("publicBrief").GetString());
        Assert.Equal(scenario.ClientDisplayName, root.GetProperty("clientDisplayName").GetString());
        Assert.Equal("intermediate", root.GetProperty("defaultDifficulty").GetString());
        string?[] difficultyIds = ["beginner", "intermediate", "advanced"];
        Assert.Equal(difficultyIds, root.GetProperty("difficulties").EnumerateArray().Select(difficulty => difficulty.GetProperty("id").GetString()));
    }

    [Fact]
    public void PublicCatalogProjectsEveryScenarioInOrder()
    {
        var projections = TestScenarios.Catalog.PublicCatalog();

        Assert.Equal(TestScenarios.ShippedIds, projections.Select(projection => projection.Id));
        Assert.All(projections, projection => Assert.Equal(3, projection.Difficulties.Count));
    }

    private static string Serialize(ScenarioDefinition scenario) =>
        JsonSerializer.Serialize(ScenarioCatalog.ToPublic(scenario), JsonSerializerOptions.Web);

    /// <summary>Unescaped property names and string values, so escaped characters cannot hide a leak.</summary>
    private static string DecodedText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var text = new StringBuilder();
        Collect(document.RootElement, text);
        return text.ToString();
    }

    private static void Collect(JsonElement element, StringBuilder text)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    text.AppendLine(property.Name);
                    Collect(property.Value, text);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, text);
                }

                break;
            case JsonValueKind.String:
                text.AppendLine(element.GetString());
                break;
        }
    }
}

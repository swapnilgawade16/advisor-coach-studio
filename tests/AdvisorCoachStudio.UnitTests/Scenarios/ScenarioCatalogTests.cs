using System.Text.Json;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.UnitTests.Scenarios;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void LoadFromDirectoryLoadsExactlyTheShippedScenariosInDocumentedOrder()
    {
        var catalog = ScenarioCatalog.LoadFromDirectory(RepoPaths.ScenariosDirectory);

        Assert.Equal(TestScenarios.ShippedIds, catalog.All.Select(scenario => scenario.Id));
        Assert.All(catalog.All, scenario => Assert.Empty(ScenarioValidator.Validate(scenario)));
        Assert.All(TestScenarios.ShippedIds, id => Assert.True(catalog.Contains(id)));
        Assert.False(catalog.Contains("unknown-scenario"));
        Assert.False(catalog.Contains(null));
        Assert.False(catalog.TryGet(null, out _));
        Assert.False(catalog.TryGet("MARKET-DECLINE", out _));
    }

    [Theory]
    [InlineData("market-decline", "Alex Morgan", "AM", "I checked my investments this morning and I'm really worried. We've lost a lot. I think I should sell everything before it gets worse.")]
    [InlineData("fees-and-trust", "Jordan Lee", "JL", "I've been looking at the fees on my statement, and I'm not sure I'm getting value for what I'm paying. Why shouldn't I move to someone cheaper?")]
    [InlineData("first-time-investor", "Taylor Reed", "TR", "Honestly, I find all of this investing stuff confusing. People keep talking about funds, risk, diversification, and I don't really know where to start.")]
    public void ShippedScenariosMatchTheSpecifiedClientAndOpening(string id, string clientName, string initials, string opening)
    {
        Assert.True(TestScenarios.Catalog.TryGet(id, out var scenario));

        Assert.Equal(clientName, scenario.ClientDisplayName);
        Assert.Equal(initials, scenario.ClientInitials);
        Assert.Equal(opening, scenario.OpeningUtterance);
        Assert.Matches(@"^\d+\.\d+\.\d+$", scenario.Version);
    }

    [Fact]
    public void ConstructorRejectsDuplicateScenarioIds()
    {
        var first = TestScenarios.Valid("duplicate-id");
        var second = TestScenarios.Valid("duplicate-id") with { Title = "Another synthetic scenario" };

        var error = Assert.Throws<InvalidOperationException>(() => new ScenarioCatalog([first, second, TestScenarios.Valid("unique-id")]));

        Assert.Contains("Scenario id 'duplicate-id' is defined more than once.", error.Message);
        Assert.DoesNotContain("'unique-id'", error.Message);
    }

    [Fact]
    public void ConstructorRejectsAnEmptyCatalogue()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ScenarioCatalog([]));

        Assert.Contains("No scenarios were found.", error.Message);
    }

    [Fact]
    public void ConstructorFailsFastOnAnInvalidScenario()
    {
        var invalid = TestScenarios.Valid() with { OpeningUtterance = " " };

        var error = Assert.Throws<InvalidOperationException>(() => new ScenarioCatalog([TestScenarios.Valid("other-scenario"), invalid]));

        Assert.Contains("synthetic-scenario: 'openingUtterance' must not be empty.", error.Message);
    }

    [Fact]
    public void LoadFromDirectoryRejectsAMissingDirectory()
    {
        var missing = Path.Combine(RepoPaths.ScenariosDirectory, "does-not-exist");

        var error = Assert.Throws<InvalidOperationException>(() => ScenarioCatalog.LoadFromDirectory(missing));

        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public void LoadFromDirectoryNamesTheInvalidFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "scratch-scenarios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var json = ShippedJson("market-decline.json");
            json["unexpectedField"] = "synthetic";
            File.WriteAllText(Path.Combine(directory, "broken-scenario.json"), json.ToJsonString());

            var error = Assert.Throws<InvalidOperationException>(() => ScenarioCatalog.LoadFromDirectory(directory));

            Assert.Contains("broken-scenario.json", error.Message);
            Assert.IsType<JsonException>(error.InnerException);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("market-decline.json")]
    [InlineData("fees-and-trust.json")]
    [InlineData("first-time-investor.json")]
    public void ShippedFilesDeserializeWithTheStrictOptions(string fileName)
    {
        var definition = JsonSerializer.Deserialize<ScenarioDefinition>(ShippedJson(fileName).ToJsonString(), ScenarioCatalog.ScenarioJsonOptions);

        Assert.NotNull(definition);
        Assert.Equal(Path.GetFileNameWithoutExtension(fileName), definition.Id);
    }

    [Fact]
    public void DeserializationRejectsUnknownRootProperties()
    {
        var json = ShippedJson("market-decline.json");
        json["unexpectedField"] = "synthetic";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioDefinition>(json.ToJsonString(), ScenarioCatalog.ScenarioJsonOptions));
    }

    [Fact]
    public void DeserializationRejectsUnknownNestedProperties()
    {
        var hiddenFact = ShippedJson("fees-and-trust.json");
        hiddenFact["hiddenFacts"]![0]!["triggerPhrase"] = "synthetic";
        var guidance = ShippedJson("fees-and-trust.json");
        guidance["difficultyGuidance"]!["expert"] = "synthetic";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioDefinition>(hiddenFact.ToJsonString(), ScenarioCatalog.ScenarioJsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioDefinition>(guidance.ToJsonString(), ScenarioCatalog.ScenarioJsonOptions));
    }

    [Theory]
    [InlineData("openingUtterance")]
    [InlineData("hiddenFacts")]
    [InlineData("difficultyGuidance")]
    public void DeserializationRejectsMissingRequiredProperties(string property)
    {
        var json = ShippedJson("first-time-investor.json");
        Assert.True(json.Remove(property));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioDefinition>(json.ToJsonString(), ScenarioCatalog.ScenarioJsonOptions));
    }

    private static JsonObject ShippedJson(string fileName) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.ScenariosDirectory, fileName)))!.AsObject();
}

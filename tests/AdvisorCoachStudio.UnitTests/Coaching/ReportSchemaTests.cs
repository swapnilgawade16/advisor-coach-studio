using System.Text.Json;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.UnitTests.Coaching;

/// <summary>The checked-in report content schema, loaded with the real scenario catalogue.</summary>
public sealed class ReportSchemaTests
{
    private static ReportSchema Schema => TestReportSchema.Shared;

    [Fact]
    public void ValidContentSatisfiesTheSchema() => Assert.Empty(Validate(ValidNode()));

    [Fact]
    public void UnscoredContentWithEmptyArraysSatisfiesTheSchema()
    {
        var content = TestReportContent.Valid() with
        {
            KeyStrength = null,
            PriorityImprovement = null,
            SkillScores = Rubric.Categories.Select(category => TestReportContent.Score(category.Id, null)).ToList(),
            Strengths = [],
            Improvements = [],
            AlternativePhrases = [],
            MissedDiscoveryQuestions = [],
        };

        Assert.Empty(Validate(JsonNode.Parse(TestReportContent.ToJson(content))!.AsObject()));
    }

    [Fact]
    public void AdditionalPropertiesAreRejectedAtEveryLevel()
    {
        var root = ValidNode();
        root["overallScore"] = 5;
        var score = ValidNode();
        score["skillScores"]![0]!["confidence"] = 0.9;
        var evidence = ValidNode();
        evidence["strengths"]![0]!["evidence"]![0]!["speaker"] = "advisor";

        Assert.NotEmpty(Validate(root));
        Assert.NotEmpty(Validate(score));
        Assert.NotEmpty(Validate(evidence));
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("keyStrength")]
    [InlineData("skillScores")]
    [InlineData("practicePlan")]
    [InlineData("warnings")]
    public void MissingRootPropertiesAreRejected(string property)
    {
        var node = ValidNode();
        Assert.True(node.Remove(property));

        Assert.NotEmpty(Validate(node));
    }

    [Fact]
    public void MissingNestedPropertiesAreRejectedEvenWhenNullable()
    {
        var interpretation = ValidNode();
        Assert.True(interpretation["strengths"]![0]!.AsObject().Remove("interpretation"));
        var evidence = ValidNode();
        Assert.True(evidence["skillScores"]![0]!.AsObject().Remove("evidence"));

        Assert.NotEmpty(Validate(interpretation));
        Assert.NotEmpty(Validate(evidence));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("3.5")]
    [InlineData("\"3\"")]
    public void ScoresOutsideTheIntegerEnumAreRejected(string score)
    {
        var node = ValidNode();
        node["skillScores"]![0]!["score"] = JsonNode.Parse(score);

        Assert.NotEmpty(Validate(node));
    }

    [Fact]
    public void UnknownRecommendedScenarioIsRejected()
    {
        var node = ValidNode();
        node["practicePlan"]!["recommendedScenarioId"] = "retirement-planning";

        Assert.NotEmpty(Validate(node));
    }

    [Fact]
    public void ValidationMessagesDoNotEchoContent()
    {
        var node = ValidNode();
        node["summary"] = 12345;
        node["practicePlan"]!["recommendedScenarioId"] = "SYNTHETIC-SECRET-VALUE";

        var errors = Validate(node);

        Assert.NotEmpty(errors);
        Assert.All(errors, error =>
        {
            Assert.DoesNotContain("SYNTHETIC-SECRET-VALUE", error);
            Assert.DoesNotContain("12345", error);
        });
    }

    [Fact]
    public void ProviderSchemaOmitsRootMetadataKeywords()
    {
        foreach (var keyword in new[] { "$schema", "$id", "title", "description" })
        {
            Assert.False(Schema.ProviderSchema.ContainsKey(keyword), $"Provider schema must not contain '{keyword}'.");
        }

        Assert.Contains("\"$schema\"", Schema.ValidationSchemaJson);
    }

    [Fact]
    public void ProviderSchemaIsStrictModeCompatible()
    {
        var checkedObjects = AssertStrictObjects(Schema.ProviderSchema, "#");

        Assert.True(checkedObjects >= 8, $"Expected every object schema to be checked, found {checkedObjects}.");
    }

    [Fact]
    public void RecommendedScenarioEnumEqualsTheCatalogIds() =>
        Assert.Equal(TestScenarios.Catalog.All.Select(scenario => scenario.Id), RecommendedScenarioEnum(Schema));

    [Fact]
    public void ConstructingASecondInstanceInTheSameProcessSucceeds()
    {
        Assert.NotNull(Schema);

        var second = new ReportSchema(RepoPaths.ReportSchemaFile, TestScenarios.Catalog);

        Assert.Empty(Validate(ValidNode(), second));
    }

    [Fact]
    public void RecommendedScenarioEnumFollowsTheLoadedCatalog()
    {
        Assert.NotNull(Schema);
        var schema = new ReportSchema(RepoPaths.ReportSchemaFile, new ScenarioCatalog([TestScenarios.Valid("synthetic-only")]));
        var shippedScenario = ValidNode();
        var syntheticScenario = ValidNode();
        syntheticScenario["practicePlan"]!["recommendedScenarioId"] = "synthetic-only";

        Assert.Equal(new[] { "synthetic-only" }, RecommendedScenarioEnum(schema));
        Assert.NotEmpty(Validate(shippedScenario, schema));
        Assert.Empty(Validate(syntheticScenario, schema));
    }

    private static JsonObject ValidNode() => JsonNode.Parse(TestReportContent.ToJson(TestReportContent.Valid()))!.AsObject();

    private static IReadOnlyList<string> Validate(JsonObject node, ReportSchema? schema = null)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return (schema ?? Schema).Validate(document.RootElement);
    }

    private static IEnumerable<string> RecommendedScenarioEnum(ReportSchema schema) =>
        schema.ProviderSchema["properties"]!["practicePlan"]!["properties"]!["recommendedScenarioId"]!["enum"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .ToList();

    /// <summary>Strict structured outputs require additionalProperties:false and every property listed as required.</summary>
    private static int AssertStrictObjects(JsonNode? node, string path)
    {
        var checkedObjects = 0;
        if (node is JsonObject schemaObject)
        {
            if (schemaObject["properties"] is JsonObject properties)
            {
                Assert.False(schemaObject["additionalProperties"]?.GetValue<bool>() ?? true, $"{path}: additionalProperties must be false.");
                var required = schemaObject["required"]!.AsArray().Select(name => name!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
                Assert.Equal(properties.Select(property => property.Key).Order(StringComparer.Ordinal).ToList(), required);
                checkedObjects++;
            }

            foreach (var (key, child) in schemaObject)
            {
                checkedObjects += AssertStrictObjects(child, $"{path}/{key}");
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                checkedObjects += AssertStrictObjects(array[i], $"{path}/{i}");
            }
        }

        return checkedObjects;
    }
}

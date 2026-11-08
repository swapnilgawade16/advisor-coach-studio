using System.Text.Json;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.Web.Features.Scenarios;
using Json.Schema;

namespace AdvisorCoachStudio.Web.Features.Coaching;

/// <summary>
/// Loads the checked-in report content schema, injects the live scenario catalogue into the
/// recommendedScenarioId enum, and exposes both the provider-ready schema and a validator.
/// </summary>
public sealed class ReportSchema
{
    public const string FileName = "coaching-report-content.v1.schema.json";

    private readonly JsonSchema _validator;

    public ReportSchema(string schemaPath, ScenarioCatalog catalog)
    {
        var root = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        var scenarioEnum = new JsonArray(catalog.All.Select(s => (JsonNode)JsonValue.Create(s.Id)!).ToArray());
        root["properties"]!["practicePlan"]!["properties"]!["recommendedScenarioId"]!["enum"] = scenarioEnum;

        ValidationSchemaJson = root.ToJsonString();

        // Register in a per-instance registry: the default global registry rejects a second schema with the same $id,
        // which breaks any second host in the same process. Meta-schema lookups still fall back to the global registry.
        _validator = JsonSchema.FromText(ValidationSchemaJson, new BuildOptions { SchemaRegistry = new SchemaRegistry() });

        // Strict structured outputs reject schema metadata keywords at the root.
        var provider = root.DeepClone().AsObject();
        provider.Remove("$schema");
        provider.Remove("$id");
        provider.Remove("title");
        provider.Remove("description");
        ProviderSchema = provider;
    }

    public string ValidationSchemaJson { get; }

    /// <summary>Schema sent to the model (strict-mode compatible).</summary>
    public JsonObject ProviderSchema { get; }

    /// <summary>Returns schema violations (empty when valid). Messages never include model content.</summary>
    public IReadOnlyList<string> Validate(JsonElement instance)
    {
        var result = _validator.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            return [];
        }

        var errors = new List<string>();
        foreach (var detail in result.Details ?? [])
        {
            if (detail.Errors is null)
            {
                continue;
            }

            foreach (var (keyword, _) in detail.Errors)
            {
                errors.Add($"{detail.InstanceLocation}: violates '{keyword}'");
            }
        }

        return errors.Count > 0 ? errors.Distinct().Take(20).ToList() : ["Output does not match the report schema."];
    }
}

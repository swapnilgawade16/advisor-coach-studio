using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdvisorCoachStudio.Web.Features.Scenarios;

/// <summary>Public projection of a scenario. Contains no hidden facts, prompt text, triggers, or scoring answers.</summary>
public sealed record PublicScenario(
    string Id,
    string Version,
    string Title,
    string PublicBrief,
    IReadOnlyList<string> SkillFocus,
    string ClientDisplayName,
    string ClientInitials,
    string SuggestedDuration,
    IReadOnlyList<PublicDifficulty> Difficulties,
    string DefaultDifficulty);

public sealed record PublicDifficulty(string Id, string Name, string Description);

/// <summary>Loads, validates, and serves the application-owned scenario files. Fails fast on invalid definitions.</summary>
public sealed class ScenarioCatalog
{
    public static readonly JsonSerializerOptions ScenarioJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    private readonly FrozenDictionary<string, ScenarioDefinition> _scenarios;
    private readonly IReadOnlyList<ScenarioDefinition> _ordered;

    public ScenarioCatalog(IEnumerable<ScenarioDefinition> scenarios)
    {
        var list = scenarios.ToList();
        var errors = list.SelectMany(ScenarioValidator.Validate).ToList();
        var duplicates = list.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        errors.AddRange(duplicates.Select(id => $"Scenario id '{id}' is defined more than once."));
        if (list.Count == 0)
        {
            errors.Add("No scenarios were found.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid scenario catalogue:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        _ordered = list;
        _scenarios = list.ToFrozenDictionary(s => s.Id, StringComparer.Ordinal);
    }

    public static ScenarioCatalog LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"Scenario directory '{directory}' does not exist.");
        }

        var definitions = new List<ScenarioDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var definition = JsonSerializer.Deserialize<ScenarioDefinition>(File.ReadAllText(file), ScenarioJsonOptions)
                    ?? throw new InvalidOperationException("File is empty.");
                definitions.Add(definition);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Scenario file '{Path.GetFileName(file)}' is not valid: {ex.Message}", ex);
            }
        }

        return new ScenarioCatalog(definitions.OrderBy(OrderKey).ThenBy(d => d.Id, StringComparer.Ordinal));
    }

    public IReadOnlyList<ScenarioDefinition> All => _ordered;

    public bool TryGet(string? id, out ScenarioDefinition scenario)
    {
        scenario = null!;
        return id is not null && _scenarios.TryGetValue(id, out scenario!);
    }

    public bool Contains(string? id) => id is not null && _scenarios.ContainsKey(id);

    public IReadOnlyList<PublicScenario> PublicCatalog() => _ordered.Select(ToPublic).ToList();

    public static PublicScenario ToPublic(ScenarioDefinition s) => new(
        s.Id,
        s.Version,
        s.Title,
        s.PublicBrief,
        s.SkillFocus,
        s.ClientDisplayName,
        s.ClientInitials,
        "Up to 2 minutes",
        DifficultyInfo.All.Select(d => new PublicDifficulty(d.Name.ToLowerInvariant(), d.Name, d.Description)).ToList(),
        "intermediate");

    // Keep the documented scenario order stable for the landing page.
    private static int OrderKey(ScenarioDefinition s) => s.Id switch
    {
        "market-decline" => 0,
        "fees-and-trust" => 1,
        "first-time-investor" => 2,
        _ => 100,
    };
}

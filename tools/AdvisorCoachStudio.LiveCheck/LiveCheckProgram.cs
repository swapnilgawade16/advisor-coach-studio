using System.Text.Json;
using System.Text.Json.Serialization;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using AdvisorCoachStudio.Web.Prompts;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AdvisorCoachStudio.LiveCheck;

/// <summary>
/// Live verification against the configured Azure deployments (no fakes or simulators). Uses the application's own
/// connector, prompt builders, event builders, event parser, and report generator, and reads the same configuration
/// as the web app (appsettings.json, user-secrets, environment variables). Never prints secrets.
/// </summary>
internal static class LiveCheckProgram
{
    private const string UserSecretsId = "advisor-coach-studio-local";

    private static async Task<int> Main(string[] args)
    {
        LiveCheckArguments arguments;
        try
        {
            arguments = LiveCheckArguments.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(LiveCheckArguments.Usage);
            return 2;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var web = Path.Combine(RepoPaths.FindRoot(), "src", "AdvisorCoachStudio.Web");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(web, "appsettings.json"), optional: true)
            .AddUserSecrets(UserSecretsId)
            .AddEnvironmentVariables()
            .Build();
        var catalog = ScenarioCatalog.LoadFromDirectory(Path.Combine(web, "Scenarios"));
        var report = arguments.Report ? ReportSettings(configuration, catalog, Path.Combine(web, "Schemas", ReportSchema.FileName), arguments.DumpResponsesDirectory) : null;
        if (arguments.Report && report is null)
        {
            return 2;
        }

        using var loggers = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var checks = new CheckLog();
        if (arguments.TranscriptPath is { } replay)
        {
            var saved = await SavedTranscript.LoadAsync(replay, cts.Token);
            if (!catalog.TryGet(saved.ScenarioId, out var savedScenario))
            {
                Console.Error.WriteLine($"The saved transcript uses unknown scenario '{saved.ScenarioId}'.");
                return 2;
            }

            Console.WriteLine($"Replaying {saved.Turns.Count} saved turns ({savedScenario.Id}, {DifficultyInfo.NameOf(saved.Difficulty)}) from {Path.GetFileName(replay)}; no voice conversation.");
            await GenerateReportAsync(report!, savedScenario, saved.Difficulty, saved.Turns, loggers, checks, cts.Token);
            checks.PrintSummary();
            return checks.Failed ? 1 : 0;
        }

        var azure = configuration.GetSection(AzureRealtimeOptions.Section).Get<AzureRealtimeOptions>() ?? new AzureRealtimeOptions();
        var realtime = configuration.GetSection(RealtimeOptions.Section).Get<RealtimeOptions>() ?? new RealtimeOptions();
        var missing = azure.Validate();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("Azure realtime settings are missing or invalid: " + string.Join(", ", missing));
            return 2;
        }

        if (!catalog.TryGet(arguments.ScenarioId, out var scenario))
        {
            Console.Error.WriteLine($"Unknown scenario '{arguments.ScenarioId}'.");
            return 2;
        }

        var instructions = RoleplayPromptBuilder.Build(scenario, arguments.Difficulty);
        Console.WriteLine($"Azure resource host: {new Uri(azure.Endpoint!).Host}");
        Console.WriteLine($"Realtime deployment: {azure.Deployment} (auth {azure.Authentication}, transcription {azure.TranscriptionModel}, voice {(string.IsNullOrWhiteSpace(azure.Voice) ? "provider default" : azure.Voice)})");
        Console.WriteLine($"Scenario: {scenario.Id} v{scenario.Version}, {DifficultyInfo.NameOf(arguments.Difficulty)}; {RoleplayPromptBuilder.Version}, {instructions.Length} chars, sha256 {TextTools.Hash(instructions)}");
        if (arguments.ShowPrompt)
        {
            Console.WriteLine("----- system instructions sent in session.update -----");
            Console.WriteLine(instructions);
            Console.WriteLine("----- end of system instructions -----");
        }

        var clips = new List<AdvisorClip>();
        foreach (var path in arguments.AdvisorWavs)
        {
            clips.Add(new AdvisorClip(Path.GetFileName(path), await WavFile.ReadPcm16Mono24kAsync(path, cts.Token)));
        }

        Console.WriteLine();
        var connector = new AzureRealtimeConnector(
            new FixedOptionsMonitor<AzureRealtimeOptions>(azure),
            new AzureTokenProvider(new DefaultAzureCredential()),
            loggers.CreateLogger<AzureRealtimeConnector>());
        var run = new LiveRun(connector, checks, scenario, instructions, azure.TranscriptionModel!, realtime, clips, arguments.Interrupt);
        try
        {
            await run.RunAsync(cts.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ProviderConnectException or OperationCanceledException)
        {
            checks.Fail("live run", ex.Message);
        }

        if (arguments.SaveTranscriptPath is { } savePath && run.Turns.Count > 0)
        {
            await new SavedTranscript(scenario.Id, arguments.Difficulty, run.Turns).SaveAsync(savePath, cts.Token);
            Console.WriteLine($"Saved the transcript ({run.Turns.Count} turns) to {savePath}.");
        }

        if (report is not null)
        {
            await GenerateReportAsync(report, scenario, arguments.Difficulty, run.Turns, loggers, checks, cts.Token);
        }

        checks.PrintSummary();
        return checks.Failed ? 1 : 0;
    }

    private sealed record ReportContext(AzureReportsOptions Options, ScenarioCatalog Catalog, string SchemaPath, string? DumpDirectory);

    private static ReportContext? ReportSettings(IConfiguration configuration, ScenarioCatalog catalog, string schemaPath, string? dumpDirectory)
    {
        var options = configuration.GetSection(AzureReportsOptions.Section).Get<AzureReportsOptions>() ?? new AzureReportsOptions();
        if (options.Validate() is { Count: > 0 } problems)
        {
            Console.Error.WriteLine("Generating a report needs the AzureReports settings, which are missing or invalid: " + string.Join(", ", problems));
            return null;
        }

        var effort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? "model default" : options.ReasoningEffort.Trim();
        Console.WriteLine($"Report deployment: {options.Deployment} (auth {options.Authentication}, reasoning effort {effort}, max {options.MaxOutputTokens} output tokens, {options.TimeoutSeconds} s timeout per request)");
        return new ReportContext(options, catalog, schemaPath, dumpDirectory);
    }

    private static async Task GenerateReportAsync(
        ReportContext report, ScenarioDefinition scenario, Difficulty difficulty, IReadOnlyList<LiveTurn> turns,
        ILoggerFactory loggers, CheckLog checks, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        if (!turns.Any(turn => turn.Speaker == TurnSpeaker.Advisor && turn.Status == TurnStatus.Final))
        {
            checks.Fail("coaching report", "no advisor speech was transcribed, so there is nothing to assess");
            return;
        }

        try
        {
            await new LiveReport(report.Options, report.Catalog, report.SchemaPath, loggers, checks, report.DumpDirectory).RunAsync(scenario, difficulty, turns, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            checks.Fail("coaching report", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>A live transcript saved for report-only replays. It contains the conversation text; keep it local.</summary>
internal sealed record SavedTranscript(string ScenarioId, Difficulty Difficulty, IReadOnlyList<LiveTurn> Turns)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, Options, cancellationToken);
    }

    public static async Task<SavedTranscript> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SavedTranscript>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException($"{path} does not contain a saved transcript.");
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using AdvisorCoachStudio.Web.Prompts;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace AdvisorCoachStudio.LiveCheck;

/// <summary>
/// Generates a coaching report from the live transcript with the application's own generator, schema, grounding
/// validator, and Azure client against the configured AzureReports deployment. Records timing and token usage of every
/// request, and the provider's error message when a request is rejected.
/// </summary>
internal sealed class LiveReport(AzureReportsOptions options, ScenarioCatalog catalog, string schemaPath, ILoggerFactory loggers, CheckLog checks, string? dumpDirectory)
{
    private const double TargetSeconds = 60;

    public async Task RunAsync(ScenarioDefinition scenario, Difficulty difficulty, IReadOnlyList<LiveTurn> conversation, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var session = new PracticeSession(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), scenario, difficulty, RoleplayPromptBuilder.Version, isSimulated: false, now);
        var turns = conversation.Select((turn, index) => new TranscriptTurn
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            ProviderItemId = turn.ItemId,
            ProviderResponseId = turn.ResponseId,
            ContentIndex = turn.ContentIndex,
            Sequence = index + 1,
            Speaker = turn.Speaker,
            Text = turn.Text,
            Status = turn.Status,
            Source = turn.Speaker == TurnSpeaker.Advisor ? TurnSource.InputTranscription : TurnSource.OutputAudioTranscript,
            FullyHeard = turn.FullyHeard,
            CreatedAtUtc = now,
        }).ToList();

        var recorder = new RecordingHandler(dumpDirectory) { InnerHandler = new SocketsHttpHandler() };
        using var http = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new AzureChatReportClient(
            new SingleClientFactory(http),
            new FixedOptionsMonitor<AzureReportsOptions>(options),
            new AzureTokenProvider(new DefaultAzureCredential()),
            loggers.CreateLogger<AzureChatReportClient>());
        var generator = new CoachingReportGenerator(client, new ReportSchema(schemaPath, catalog), catalog, TimeProvider.System, loggers.CreateLogger<CoachingReportGenerator>());

        var effort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? "model default" : options.ReasoningEffort.Trim();
        checks.Info("report input", $"{turns.Count} transcript turns, {turns.Count(t => t.IsQuotable)} quotable (interrupted client speech never is); deployment {options.Deployment}, reasoning effort {effort}, max {options.MaxOutputTokens} output tokens");
        var timer = Stopwatch.StartNew();
        CoachingReport report;
        try
        {
            report = await generator.GenerateAsync(session, turns, revision: 1, transcriptHash: null, cancellationToken);
        }
        catch (Exception ex) when (ex is ReportGenerationException or ReportProviderException)
        {
            ReportCalls(recorder);
            var code = ex is ReportGenerationException generation ? generation.Code : ((ReportProviderException)ex).Failure.ToString();
            var providerError = recorder.Calls.LastOrDefault(call => call.Error is not null)?.Error;
            checks.Fail("coaching report", $"{code} after {timer.Elapsed.TotalSeconds:F1} s{(providerError is null ? "" : $"; Azure said: {providerError}")}");
            return;
        }

        var elapsed = timer.Elapsed.TotalSeconds;
        ReportCalls(recorder);
        var detail = $"{options.Deployment} returned a schema-valid report whose quotes all match the transcript exactly, in {elapsed:F1} s ({recorder.Calls.Count} request(s))";
        if (elapsed <= TargetSeconds)
        {
            checks.Pass("coaching report", detail);
        }
        else
        {
            checks.Warn("coaching report", $"{detail}; slower than the {TargetSeconds:F0} s target");
        }

        Print(report);
    }

    private void ReportCalls(RecordingHandler recorder)
    {
        var number = 0;
        foreach (var call in recorder.Calls)
        {
            number++;
            var outcome = call.Error is null
                ? $"finish {call.FinishReason ?? "-"}, {call.PromptTokens} prompt + {call.CompletionTokens} completion tokens ({call.ReasoningTokens ?? 0} reasoning)"
                : call.Error;
            checks.Info($"report request {number}", $"HTTP {call.Status} in {call.Elapsed.TotalSeconds:F1} s: {outcome}");
        }
    }

    private void Print(CoachingReport report)
    {
        var content = report.Content;
        var overall = report.OverallScore?.ToString("0.0", CultureInfo.InvariantCulture) ?? "not scored";
        checks.Info("report assessment", $"{report.AssessmentStatus}, overall {overall} / 5 from {report.ScoredCategoryCount} scored skills and {report.UsableAdvisorTurnCount} usable advisor turns");
        var quotes = content.SkillScores.Sum(s => s.Evidence.Count) + content.Strengths.Sum(s => s.Evidence.Count) +
                     content.Improvements.Sum(i => i.Evidence.Count) + content.AlternativePhrases.Count;
        checks.Info("report content", $"{content.Strengths.Count} strength(s), {content.Improvements.Count} improvement(s), {content.AlternativePhrases.Count} alternative phrase(s), {content.MissedDiscoveryQuestions.Count} missed discovery question(s), {quotes} quote(s) verified");

        Console.WriteLine();
        Console.WriteLine("Coaching report (generated by Azure, validated and scored by the application):");
        Console.WriteLine($"  Summary: {content.Summary}");
        Console.WriteLine($"  Key strength: {content.KeyStrength ?? "-"}");
        Console.WriteLine($"  Priority improvement: {content.PriorityImprovement ?? "-"}");
        Console.WriteLine("  Skill scores: " + string.Join("; ", content.SkillScores.Select(s => $"{Rubric.DisplayName(s.CategoryId)} {s.Score?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}")));
        foreach (var strength in content.Strengths.Take(2))
        {
            Console.WriteLine($"  Strength: {strength.Title} | Evidence: \"{(strength.Evidence.Count > 0 ? strength.Evidence[0].Quote : "")}\"");
        }

        foreach (var improvement in content.Improvements.Take(2))
        {
            Console.WriteLine($"  Improve: {improvement.Title} | {improvement.SuggestedAdjustment}");
        }

        foreach (var phrase in content.AlternativePhrases.Take(2))
        {
            Console.WriteLine($"  Instead of \"{phrase.OriginalEvidence.Quote}\", try \"{phrase.SuggestedPhrase}\"");
        }

        foreach (var question in content.MissedDiscoveryQuestions.Take(2))
        {
            Console.WriteLine($"  Missed question: {question.Question}");
        }

        Console.WriteLine($"  Practice next: {content.PracticePlan.Exercise}");
        if (content.Warnings.Count > 0)
        {
            Console.WriteLine($"  Warnings: {string.Join(" | ", content.Warnings)}");
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

internal sealed record ReportCall(int Status, TimeSpan Elapsed, string? FinishReason, int? PromptTokens, int? CompletionTokens, int? ReasoningTokens, string? Error);

/// <summary>
/// Records metadata of each report request and optionally writes the raw response to a directory; the response body
/// is buffered and handed on unchanged.
/// </summary>
internal sealed class RecordingHandler(string? dumpDirectory) : DelegatingHandler
{
    private readonly ConcurrentQueue<ReportCall> _calls = new();

    public IReadOnlyCollection<ReportCall> Calls => _calls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _calls.Enqueue(Describe((int)response.StatusCode, timer.Elapsed, body));
        if (dumpDirectory is not null)
        {
            Directory.CreateDirectory(dumpDirectory);
            await File.WriteAllTextAsync(Path.Combine(dumpDirectory, $"report-response-{_calls.Count}.json"), body, cancellationToken);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        response.Content.Dispose();
        response.Content = new StringContent(body, Encoding.UTF8, mediaType);
        return response;
    }

    private static ReportCall Describe(int status, TimeSpan elapsed, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (status is >= 200 and < 300)
            {
                var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 ? choices[0] : default;
                var usage = Json.Obj(root, "usage");
                return new ReportCall(status, elapsed, Json.Str(choice, "finish_reason"), Json.Int(usage, "prompt_tokens"), Json.Int(usage, "completion_tokens"),
                    Json.Int(Json.Obj(usage, "completion_tokens_details"), "reasoning_tokens"), null);
            }

            var error = Json.Obj(root, "error");
            return new ReportCall(status, elapsed, null, null, null, null, $"{Json.Str(error, "code") ?? "-"}: {Json.Str(error, "message") ?? "(no message)"}");
        }
        catch (JsonException)
        {
            return new ReportCall(status, elapsed, null, null, null, null, "(the response was not JSON)");
        }
    }
}

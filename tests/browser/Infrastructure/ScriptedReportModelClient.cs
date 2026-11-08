using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// Scripted stand-in for the Azure report deployment. Every model call is parked as a <see cref="PendingReportCall"/>
/// (a TaskCompletionSource) until the test completes, fails, or holds it, so queued/generating/failed/ready states are
/// observable deterministically in the browser.
/// </summary>
public sealed class ScriptedReportModelClient : IReportModelClient
{
    private readonly Channel<PendingReportCall> _calls = Channel.CreateUnbounded<PendingReportCall>();
    private readonly ConcurrentDictionary<PendingReportCall, byte> _open = new();
    private int _callCount;

    public string DeploymentName => "browser-test-reports";

    /// <summary>Number of model calls made so far (the product skips the model when no advisor turn is usable).</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    public async Task<ReportModelResult> CompleteAsync(IReadOnlyList<ReportChatMessage> messages, JsonObject schema, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        var call = new PendingReportCall(messages);
        _open[call] = 0;
        try
        {
            // Discarding the session (or stopping the host) cancels the worker's token; surface it like a real client.
            using var registration = cancellationToken.Register(call.Cancel);
            await _calls.Writer.WriteAsync(call, CancellationToken.None);
            return await call.Result;
        }
        finally
        {
            _open.TryRemove(call, out _);
        }
    }

    /// <summary>Waits until the report worker calls the model and returns that (still open) call.</summary>
    public async Task<PendingReportCall> NextCallAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var call = await _calls.Reader.ReadAsync(cts.Token);
                if (!call.IsCompleted)
                {
                    return call;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The report worker did not call the report model within {timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>Fails every call still open (for example after a failed test) so no report worker slot stays occupied.</summary>
    public void FailOpenCalls()
    {
        foreach (var call in _open.Keys)
        {
            call.Fail();
        }

        while (_calls.Reader.TryRead(out _))
        {
        }
    }
}

/// <summary>One parked report model call.</summary>
public sealed class PendingReportCall
{
    private readonly TaskCompletionSource<ReportModelResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingReportCall(IReadOnlyList<ReportChatMessage> messages)
    {
        // The first user message is "<instruction line>\n<JSON payload>" (CoachingPromptBuilder.UserMessage).
        var user = messages.First(m => m.Role == "user").Content;
        var payload = JsonNode.Parse(user[(user.IndexOf('\n', StringComparison.Ordinal) + 1)..])!.AsObject();
        ScenarioId = payload["scenario"]!["id"]!.GetValue<string>();
        Turns = payload["transcript"]!.AsArray()
            .Select(node => new PayloadTurn(
                node!["turnId"]!.GetValue<string>(),
                node["speaker"]!.GetValue<string>(),
                node["quotable"]!.GetValue<bool>(),
                node["text"]!.GetValue<string>()))
            .ToList();
    }

    public string ScenarioId { get; }

    /// <summary>The transcript exactly as the report model sees it (labels T1, T2, ...).</summary>
    public IReadOnlyList<PayloadTurn> Turns { get; }

    public bool IsCompleted => _result.Task.IsCompleted;

    internal Task<ReportModelResult> Result => _result.Task;

    public bool Mentions(string text) => Turns.Any(turn => turn.Text.Contains(text, StringComparison.Ordinal));

    /// <summary>Returns schema-valid, evidence-grounded content built from the transcript.</summary>
    public void Succeed() => _result.TrySetResult(new ReportModelResult(ScriptedReportContent.Build(ScenarioId, Turns), "stop", null));

    /// <summary>Fails the call; BadRequest is non-transient, so the report becomes failed (and retryable) immediately.</summary>
    public void Fail(ReportFailure failure = ReportFailure.BadRequest) =>
        _result.TrySetException(new ReportProviderException(failure, "Scripted browser-test failure."));

    internal void Cancel() => _result.TrySetCanceled();
}

public sealed record PayloadTurn(string TurnId, string Speaker, bool Quotable, string Text);

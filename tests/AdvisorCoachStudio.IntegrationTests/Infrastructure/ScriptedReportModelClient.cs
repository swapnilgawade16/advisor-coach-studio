using System.Text.Json;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>A transcript turn exactly as the report model receives it (from the coaching prompt's JSON data).</summary>
public sealed record ModelTurn(string TurnId, string Speaker, string Status, bool Quotable, string Text);

/// <summary>One recorded report-model call.</summary>
public sealed record ReportCall(int Number, IReadOnlyList<ReportChatMessage> Messages, JsonObject Schema)
{
    /// <summary>True for the bounded correction request that follows rejected output.</summary>
    public bool IsCorrection => Messages.Count > 2;

    public string TranscriptMessage => Messages.First(m => m.Role == "user").Content;

    public IReadOnlyList<ModelTurn> Transcript
    {
        get
        {
            var message = TranscriptMessage;
            using var document = JsonDocument.Parse(message[(message.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
            return
            [
                .. document.RootElement.GetProperty("transcript").EnumerateArray().Select(turn => new ModelTurn(
                    turn.GetProperty("turnId").GetString()!,
                    turn.GetProperty("speaker").GetString()!,
                    turn.GetProperty("status").GetString()!,
                    turn.GetProperty("quotable").GetBoolean(),
                    turn.GetProperty("text").GetString()!)),
            ];
        }
    }
}

/// <summary>A report-model response held back until the test releases it (for discard and duplicate-job races).</summary>
public sealed class ReportGate
{
    private readonly TaskCompletionSource<ReportCall> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ReportModelResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ReportGate(bool ignoreCancellation) => IgnoreCancellation = ignoreCancellation;

    /// <summary>When true the response is returned even after the worker's token is cancelled (a late provider reply).</summary>
    public bool IgnoreCancellation { get; }

    public Task<ReportCall> Started => _started.Task;

    public void Release(ReportModelResult result) => _result.TrySetResult(result);

    internal async Task<ReportModelResult> RunAsync(ReportCall call, CancellationToken cancellationToken)
    {
        _started.TrySetResult(call);
        return IgnoreCancellation ? await _result.Task : await _result.Task.WaitAsync(cancellationToken);
    }

    internal void Abandon()
    {
        _started.TrySetCanceled();
        _result.TrySetCanceled();
    }
}

/// <summary>
/// Provider-boundary replacement for the Azure chat report client. Returns queued results in order (content JSON,
/// refusals, finish reasons, exceptions, or gated responses) and records every call. A call with nothing queued waits
/// for the next queued result (or cancellation), so unexpected calls never invent output.
/// </summary>
public sealed class ScriptedReportModelClient : IReportModelClient
{
    private readonly Lock _gate = new();
    private readonly Queue<Func<ReportCall, CancellationToken, Task<ReportModelResult>>> _steps = new();
    private readonly List<ReportGate> _gates = [];
    private readonly History<ReportCall> _calls = new();
    private TaskCompletionSource _stepAdded = Wait.NewSignal();
    private int _callNumber;
    private bool _shutdown;

    public string DeploymentName => "fake-report-deployment";

    public IReadOnlyList<ReportCall> Calls => _calls.Snapshot();

    public int CallCount => _calls.Count;

    public static ReportModelResult Content(string json) => new(json, "stop", null);

    public void Enqueue(ReportModelResult result) => AddStep((_, _) => Task.FromResult(result));

    /// <summary>Builds the result from the call (for example from the transcript labels the model received).</summary>
    public void Enqueue(Func<ReportCall, ReportModelResult> factory) => AddStep((call, _) => Task.FromResult(factory(call)));

    public void EnqueueException(Exception exception) => AddStep((_, _) => Task.FromException<ReportModelResult>(exception));

    public ReportGate EnqueueGate(bool ignoreCancellation = false)
    {
        var gate = new ReportGate(ignoreCancellation);
        lock (_gate)
        {
            _gates.Add(gate);
        }

        AddStep(gate.RunAsync);
        return gate;
    }

    public async Task<ReportCall> WaitForCallAsync(int number, TimeSpan? timeout = null)
    {
        var calls = await _calls.WaitForMatchesAsync(_ => true, number, timeout, () => $"Expected report-model call #{number}; observed {CallCount}.");
        return calls[number - 1];
    }

    public async Task<ReportModelResult> CompleteAsync(IReadOnlyList<ReportChatMessage> messages, JsonObject schema, CancellationToken cancellationToken)
    {
        var call = new ReportCall(Interlocked.Increment(ref _callNumber), [.. messages], schema);
        _calls.Add(call);
        var step = await NextStepAsync(cancellationToken);
        return await step(call, cancellationToken);
    }

    /// <summary>Abandons pending gates and waiting calls so background report work ends at test teardown.</summary>
    public void Shutdown()
    {
        TaskCompletionSource signal;
        List<ReportGate> gates;
        lock (_gate)
        {
            _shutdown = true;
            gates = [.. _gates];
            signal = _stepAdded;
            _stepAdded = Wait.NewSignal();
        }

        signal.TrySetResult();
        foreach (var gate in gates)
        {
            gate.Abandon();
        }
    }

    private void AddStep(Func<ReportCall, CancellationToken, Task<ReportModelResult>> step)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _steps.Enqueue(step);
            signal = _stepAdded;
            _stepAdded = Wait.NewSignal();
        }

        signal.TrySetResult();
    }

    private async Task<Func<ReportCall, CancellationToken, Task<ReportModelResult>>> NextStepAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task added;
            lock (_gate)
            {
                if (_steps.TryDequeue(out var step))
                {
                    return step;
                }

                if (_shutdown)
                {
                    throw new OperationCanceledException("The scripted report client was shut down.");
                }

                added = _stepAdded.Task;
            }

            await added.WaitAsync(cancellationToken);
        }
    }
}

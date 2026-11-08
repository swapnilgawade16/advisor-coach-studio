using System.Text.Json.Nodes;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>Report model double that returns scripted results in order and records every request.</summary>
internal sealed class ScriptedReportModelClient(params ReportModelResult[] results) : IReportModelClient
{
    public const string Deployment = "synthetic-report-deployment";

    private readonly Queue<ReportModelResult> _results = new(results);

    /// <summary>Snapshot of the messages sent on each call (the generator reuses and grows one list).</summary>
    public List<IReadOnlyList<ReportChatMessage>> Calls { get; } = [];

    public List<JsonObject> Schemas { get; } = [];

    public string DeploymentName => Deployment;

    public Task<ReportModelResult> CompleteAsync(IReadOnlyList<ReportChatMessage> messages, JsonObject schema, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(messages.ToList());
        Schemas.Add(schema);
        return _results.TryDequeue(out var result)
            ? Task.FromResult(result)
            : throw new InvalidOperationException("The report model was called more times than scripted.");
    }
}

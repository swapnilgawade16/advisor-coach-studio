using AdvisorCoachStudio.IntegrationTests.Infrastructure;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Report generation, grounding validation, retry, and missing configuration (spec 11.3, 11.4, 18.2).</summary>
public sealed class ReportTests
{
    [Fact]
    public async Task MissingReportConfigurationPreservesAnOtherwiseValidRoleplay()
    {
        await using var app = await TestApp.StartAsync(new TestAppOptions { ReportsConfigured = false });
        using var user = await app.LoginAsync();
        var capabilities = await user.GetAsync("/api/capabilities");
        Assert.Equal("ready", capabilities.Json.GetProperty("realtime").GetProperty("status").GetString());
        Assert.Equal("setup_required", capabilities.Json.GetProperty("reports").GetProperty("status").GetString());

        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.PlayConversationAsync(exchanges: 1);
        var ended = await roleplay.EndAsync();

        Assert.Equal("user_ended", ended.String("reason"));
        Assert.Equal("complete", ended.String("transcriptStatus"));
        Assert.Equal("setup_required", ended.String("reportStatus"));

        var report = await user.GetReportAsync(roleplay.SessionId);
        Assert.Equal("setup_required", report.String("status"));
        Assert.False(report.Json.GetProperty("retryable").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, report.Json.GetProperty("report").ValueKind);

        var transcript = await user.GetTranscriptAsync(roleplay.SessionId);
        Assert.Equal("complete", transcript.String("status"));
        Assert.Equal(3, transcript.Json.GetProperty("turns").GetArrayLength());

        var retry = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/report/retry");
        Assert.Equal((503, "provider_not_configured"), (retry.StatusCode, retry.ProblemCode));
        Assert.Equal(0, app.Reports.CallCount);
    }

    [Fact]
    public async Task HallucinatedQuotesAreRejectedAndARetryProducesExactlyOneNewModelCall()
    {
        await using var app = await TestApp.StartAsync();
        app.Reports.Enqueue(call => ScriptedReportModelClient.Content(ReportContent.Hallucinated(ReportContent.FromCall(call))));
        app.Reports.Enqueue(call => ScriptedReportModelClient.Content(ReportContent.Hallucinated(ReportContent.FromCall(call))));
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.PlayConversationAsync(exchanges: 1);
        await roleplay.EndAsync();

        // Invented evidence fails validation, one bounded correction is attempted, then the report fails safely.
        var failed = await user.WaitForReportStatusAsync(roleplay.SessionId, "failed");
        Assert.Equal("invalid_report_output", failed.String("errorCode"));
        Assert.True(failed.Json.GetProperty("retryable").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, failed.Json.GetProperty("report").ValueKind);
        Assert.Equal(2, app.Reports.CallCount);
        var correction = app.Reports.Calls[1];
        Assert.True(correction.IsCorrection);
        Assert.Contains("rejected by validation", correction.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains(ReportContent.InventedQuote, correction.Messages[^2].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportContent.InventedQuote, correction.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(3, (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").GetArrayLength());

        // A user retry queues one job for the same frozen revision; retrying again while it runs adds nothing.
        var gate = app.Reports.EnqueueGate();
        var retry = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/report/retry");
        Assert.Equal(202, retry.StatusCode);
        Assert.Contains(retry.String("status"), new[] { "queued", "generating" });
        var call = await Wait.ForAsync(gate.Started, "the retried report model call");
        Assert.False(call.IsCorrection);
        var duplicate = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/report/retry");
        Assert.Equal(202, duplicate.StatusCode);
        Assert.Equal("generating", duplicate.String("status"));
        Assert.Equal(3, app.Reports.CallCount);

        gate.Release(ScriptedReportModelClient.Content(ReportContent.Valid(ReportContent.FromCall(call))));
        var ready = await user.WaitForReportStatusAsync(roleplay.SessionId, "ready");
        Assert.Equal(1, ready.Json.GetProperty("report").GetProperty("transcriptRevision").GetInt32());
        Assert.Equal(3, app.Reports.CallCount);
        var afterReady = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/report/retry");
        Assert.Equal((409, "report_ready"), (afterReady.StatusCode, afterReady.ProblemCode));
    }

    [Fact]
    public async Task RefusedReportIsFinalAndCannotBeRetried()
    {
        await using var app = await TestApp.StartAsync();
        app.Reports.Enqueue(new ReportModelResult(null, "stop", "I can't help with that."));
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.PlayConversationAsync(exchanges: 1);
        await roleplay.EndAsync();

        var failed = await user.WaitForReportStatusAsync(roleplay.SessionId, "failed");
        Assert.Equal("report_refused", failed.String("errorCode"));
        Assert.False(failed.Json.GetProperty("retryable").GetBoolean());

        var retry = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/report/retry");
        Assert.Equal((409, "report_not_retryable"), (retry.StatusCode, retry.ProblemCode));
        Assert.Single(app.Reports.Calls);
        Assert.Equal(3, (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").GetArrayLength());
    }

    [Fact]
    public async Task TransientThrottlingIsRetriedWithBoundedBackoffBeforeSucceeding()
    {
        await using var app = await TestApp.StartAsync();
        app.Reports.EnqueueException(new ReportProviderException(ReportFailure.Throttled, "Synthetic throttling.", TimeSpan.FromMilliseconds(100)));
        app.Reports.Enqueue(call => ScriptedReportModelClient.Content(ReportContent.Valid(ReportContent.FromCall(call))));
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.PlayConversationAsync(exchanges: 1);
        await roleplay.EndAsync();

        var ready = await user.WaitForReportStatusAsync(roleplay.SessionId, "ready");

        Assert.Equal(2, app.Reports.CallCount);
        Assert.All(app.Reports.Calls, call => Assert.False(call.IsCorrection));
        Assert.Equal(1, ready.Json.GetProperty("report").GetProperty("transcriptRevision").GetInt32());
    }

    [Fact]
    public async Task UserRetriesAreBoundedPerSession()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.PlayConversationAsync(exchanges: 1);
        var invalid = new ReportModelResult("{ \"not\": \"a report\" }", "stop", null);
        for (var i = 0; i < 2 * PracticeSession.MaxReportRequests; i++)
        {
            app.Reports.Enqueue(invalid);
        }

        await roleplay.EndAsync();
        await user.WaitForReportStatusAsync(roleplay.SessionId, "failed");
        for (var retryNumber = 1; retryNumber < PracticeSession.MaxReportRequests; retryNumber++)
        {
            var accepted = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/report/retry");
            Assert.Equal(202, accepted.StatusCode);
            await app.Reports.WaitForCallAsync(2 * (retryNumber + 1));
            await user.WaitForReportStatusAsync(roleplay.SessionId, "failed");
        }

        var refused = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/report/retry");
        Assert.Equal((429, "retry_limit_reached"), (refused.StatusCode, refused.ProblemCode));
        Assert.Equal(2 * PracticeSession.MaxReportRequests, app.Reports.CallCount);
    }
}

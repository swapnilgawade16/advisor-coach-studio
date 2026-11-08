using Microsoft.Playwright;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// Base for report-state tests on the Azure-mode host. Every model call is parked in
/// <see cref="ScriptedReportModelClient"/>; calls left open by a test are failed afterwards so the report worker's two
/// slots are always free for the next test.
/// </summary>
public abstract class AzureReportTest(BrowserFixture browsers, AzureAppHost host) : BrowserTest(browsers, host)
{
    protected static readonly TimeSpan ModelCallTimeout = TimeSpan.FromSeconds(45);

    protected AzureAppHost AzureHost => host;

    protected ScriptedReportModelClient Reports => host.Reports;

    public override ValueTask InitializeAsync()
    {
        host.Reports.FailOpenCalls();
        return base.InitializeAsync();
    }

    protected override ValueTask AfterContextsClosedAsync()
    {
        host.Reports.FailOpenCalls();
        return ValueTask.CompletedTask;
    }

    /// <summary>Starts a practice session whose microphone plays voice-like bursts and ends it after advisor turns.</summary>
    protected async Task<(IPage Page, Guid SessionId)> PractiseWithAdvisorTurnsAsync(string scenarioId, int advisorTurns)
    {
        var page = await OpenPageAsync(MicrophoneMode.SpeechBursts);
        var sessionId = await PracticeFlow.StartSessionAsync(page, scenarioId);
        await PracticeFlow.WaitForAdvisorTurnsAsync(page, advisorTurns);
        await PracticeFlow.EndAsync(page);
        return (page, sessionId);
    }
}

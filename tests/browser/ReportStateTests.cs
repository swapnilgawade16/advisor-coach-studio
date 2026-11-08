using AdvisorCoachStudio.BrowserTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>Spec 18.3: queued, generating, and failed report states (Azure-mode host with a scripted report model).</summary>
public sealed class ReportStateTests(BrowserFixture browsers, AzureAppHost host) : AzureReportTest(browsers, host)
{
    [Fact]
    public async Task QueuedThenGeneratingAreShownWhileTheReportModelIsGated()
    {
        // Two unrelated ended sessions hold both report worker slots at the (gated) model.
        var blockers = new[] { AzureHost.QueueBackgroundReport("Background advisor turn A."), AzureHost.QueueBackgroundReport("Background advisor turn B.") };
        try
        {
            var held = new[] { await Reports.NextCallAsync(ModelCallTimeout), await Reports.NextCallAsync(ModelCallTimeout) };
            Assert.All(held, call => Assert.True(call.Mentions("Background advisor turn"), "Expected the background sessions' calls first."));
            var callsWhileBlocked = Reports.CallCount;

            var (page, sessionId) = await PractiseWithAdvisorTurnsAsync("market-decline", advisorTurns: 1);
            await PracticeFlow.OpenReportAsync(page, sessionId);
            await PracticeFlow.WaitForReportStatusAsync(page, "queued");
            await Expect(page.Locator("#report-status-text")).ToHaveTextAsync("Your coaching report is queued.");
            await Expect(page.Locator("#report-root")).ToHaveAttributeAsync("data-pending", "true");
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Try generating the report again" })).ToHaveCountAsync(0);
            await Expect(page.Locator("#report-transcript li.turn-advisor").First).ToBeVisibleAsync();
            Assert.Equal(callsWhileBlocked, Reports.CallCount);

            // Free one slot: this session's job reaches the model, which keeps it waiting.
            AzureHost.Discard(blockers[0]);
            var call = await Reports.NextCallAsync(ModelCallTimeout);
            Assert.False(call.Mentions("Background advisor turn"));
            Assert.Contains(call.Turns, turn => turn.Speaker == "advisor" && turn.Quotable);
            await PracticeFlow.WaitForReportStatusAsync(page, "generating");
            await Expect(page.Locator("#report-status-text")).ToHaveTextAsync("Your coaching report is being generated.");
            await Expect(page.Locator("#report-root")).ToHaveAttributeAsync("data-pending", "true");

            call.Succeed();
            await PracticeFlow.WaitForReportStatusAsync(page, "ready");
            await Expect(page.Locator("#report-root")).ToHaveAttributeAsync("data-pending", "false");
            await Expect(page.Locator("#report-status-text")).ToHaveCountAsync(0);
            await Expect(page.GetByText(ScriptedReportContent.Summary)).ToBeVisibleAsync();
        }
        finally
        {
            foreach (var blocker in blockers)
            {
                AzureHost.Discard(blocker);
            }
        }
    }

    [Fact]
    public async Task FailedReportOffersARetryThatLeadsToAReadyReport()
    {
        var (page, sessionId) = await PractiseWithAdvisorTurnsAsync("fees-and-trust", advisorTurns: 1);
        var first = await Reports.NextCallAsync(ModelCallTimeout);
        await PracticeFlow.OpenReportAsync(page, sessionId);
        await PracticeFlow.WaitForReportStatusAsync(page, "generating");

        first.Fail();
        await PracticeFlow.WaitForReportStatusAsync(page, "failed");
        await Expect(page.Locator("#report-status-text")).ToHaveTextAsync("The coaching report could not be generated. Please try again.");
        await Expect(page.Locator("#report-root")).ToHaveAttributeAsync("data-pending", "false");
        var retry = page.GetByRole(AriaRole.Button, new() { Name = "Try generating the report again" });
        await Expect(retry).ToBeVisibleAsync();
        await Expect(page.Locator("#report-transcript li").First).ToBeVisibleAsync();

        await retry.ClickAsync();
        var second = await Reports.NextCallAsync(ModelCallTimeout);
        Assert.Equal(first.Turns, second.Turns);
        await PracticeFlow.WaitForReportStatusAsync(page, "generating");
        await Expect(retry).ToHaveCountAsync(0);

        second.Succeed();
        await PracticeFlow.WaitForReportStatusAsync(page, "ready");
        await Expect(page.GetByText(ScriptedReportContent.Summary)).ToBeVisibleAsync();
        await Expect(page.Locator(".skill")).ToHaveCountAsync(6);
        await Expect(retry).ToHaveCountAsync(0);
        Assert.Empty(PageErrors);
    }
}

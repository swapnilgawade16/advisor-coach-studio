using AdvisorCoachStudio.BrowserTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>Spec 18.3: the demonstration has no microphone input and cannot change the roleplay report.</summary>
public sealed class DemonstrationTests(BrowserFixture browsers, SimulationAppHost host) : BrowserTest(browsers, host)
{
    // Report sections (everything except the demonstration panel and the action buttons), as text.
    private const string ReportContentScript = """
        () => [...document.querySelectorAll('#report-root > section:not(#demo-panel):not(.actions)')]
          .map(section => section.textContent.replace(/\s+/g, ' ').trim())
          .join('\n')
        """;

    [Fact]
    public async Task DemonstrationPlaysWithoutTheMicrophoneAndLeavesTheReportUnchanged()
    {
        var page = await OpenPageAsync();
        var sessionId = await PracticeFlow.StartSessionAsync(page);
        await PracticeFlow.WaitForListeningAsync(page);
        await PracticeFlow.EndAsync(page);
        await PracticeFlow.OpenReportAsync(page, sessionId);
        await PracticeFlow.WaitForReportStatusAsync(page, "ready");

        var reportBefore = (await Api.GetAsync(page, $"/api/sessions/{sessionId}/report")).GetRawText();
        var transcriptBefore = (await Api.GetAsync(page, $"/api/sessions/{sessionId}/transcript")).GetRawText();
        var contentBefore = await page.EvaluateAsync<string>(ReportContentScript);
        Assert.Contains("Coaching report", await page.InnerTextAsync("h1"), StringComparison.Ordinal);
        Assert.Equal(0, await page.GetUserMediaCallsAsync());

        await Expect(page.Locator("#demo-panel")).ToBeHiddenAsync();
        await page.ClickAsync("#demo-open");
        await Expect(page.Locator("#demo-panel")).ToBeVisibleAsync();
        await Expect(page.Locator("#demo-start")).ToBeFocusedAsync();
        await Expect(page.Locator("#demo-state")).ToHaveTextAsync("Not started");

        await page.ClickAsync("#demo-start");
        await Expect(page.Locator("#demo-stop")).ToBeEnabledAsync();
        var demoTranscript = page.Locator("#demo-transcript p").First;
        await Expect(demoTranscript).ToContainTextAsync("[Simulated demonstration]", new() { Timeout = PracticeFlow.LiveTimeoutMs });
        await Expect(page.Locator("#demo-state")).ToHaveTextAsync("Demonstration finished", new() { Timeout = 60_000 });
        await Expect(page.Locator("#demo-start")).ToHaveTextAsync("Play a new demonstration");
        await Expect(page.Locator("#demo-start")).ToBeEnabledAsync();
        await Expect(page.Locator("#demo-stop")).ToBeDisabledAsync();
        await Expect(page.Locator("#demo-error")).ToBeHiddenAsync();
        await Expect(demoTranscript).ToContainTextAsync("what matters most to you right now?");

        var probe = await page.SnapshotAsync();
        Assert.Equal(0, probe.GetUserMediaCalls);
        Assert.Empty(probe.TrackStates);
        Assert.Equal(0, probe.AudioAppends);
        Assert.DoesNotContain(probe.Worklets, worklet => worklet.Name == "acs-capture");
        Assert.Contains(probe.Worklets, worklet => worklet.Name == "acs-playback");
        Assert.Single(probe.SocketUrls);
        Assert.Contains("/ws/demonstrations/", probe.SocketUrls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.muted", probe.Sent);

        // The roleplay report and transcript are untouched, on the page, in the API, and after a reload.
        Assert.Equal(contentBefore, await page.EvaluateAsync<string>(ReportContentScript));
        Assert.Equal(reportBefore, (await Api.GetAsync(page, $"/api/sessions/{sessionId}/report")).GetRawText());
        Assert.Equal(transcriptBefore, (await Api.GetAsync(page, $"/api/sessions/{sessionId}/transcript")).GetRawText());
        await page.ReloadAsync();
        await PracticeFlow.WaitForReportStatusAsync(page, "ready");
        Assert.Equal(contentBefore, await page.EvaluateAsync<string>(ReportContentScript));
        Assert.DoesNotContain("[Simulated demonstration]", await page.Locator("#report-transcript").TextContentAsync() ?? "", StringComparison.Ordinal);
        Assert.Empty(PageErrors);
    }
}

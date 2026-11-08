using System.Text.RegularExpressions;
using AdvisorCoachStudio.BrowserTests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>Spec 18.3: reload or navigation during a live conversation does not leak a live microphone stream.</summary>
public sealed partial class ReloadTests(BrowserFixture browsers, SimulationAppHost host) : BrowserTest(browsers, host)
{
    [Fact]
    public async Task ReloadDuringALiveSessionStopsTheMicrophoneAndTheSessionCannotBeRejoined()
    {
        var page = await OpenPageAsync();
        var sessionId = await PracticeFlow.StartSessionAsync(page);
        await page.WaitForAudioAppendsAboveAsync(0);
        Assert.Equal(["live"], (await page.SnapshotAsync()).TrackStates);

        await page.ReloadAsync();
        await Expect(page).ToHaveURLAsync(new Regex($"/practice/{sessionId}$"));

        await AssertPreviousDocumentReleasedEverythingAsync(page);

        // The old conversation is over on the server and the page never offers to rejoin it.
        await Expect(page.Locator("#join-button")).ToHaveCountAsync(0);
        await Expect(page.Locator("#live")).ToHaveCountAsync(0);
        await WaitUntilEndedAsync(page, sessionId);
        await page.ReloadAsync();
        await Expect(page.Locator("#ended-heading")).ToHaveTextAsync("This practice conversation has ended");
        await Expect(page.Locator($"#session-root a[href='/sessions/{sessionId}/report']")).ToBeVisibleAsync();
        Assert.Equal(0, await page.GetUserMediaCallsAsync());
    }

    [Fact]
    public async Task NavigatingAwayDuringALiveSessionStopsTheMicrophoneAndEndsTheSession()
    {
        var page = await OpenPageAsync();
        var sessionId = await PracticeFlow.StartSessionAsync(page, "fees-and-trust");
        await page.WaitForAudioAppendsAboveAsync(0);

        await page.ClickAsync("header nav a[href='/']");
        await Expect(page.Locator("#scenarios-heading")).ToBeVisibleAsync();

        await AssertPreviousDocumentReleasedEverythingAsync(page);
        await WaitUntilEndedAsync(page, sessionId);
        await page.ReloadAsync();
        await Expect(page.Locator(".current-session .status")).ToHaveTextAsync(EndedStatus());
        await Expect(page.Locator($".current-session a[href='/sessions/{sessionId}/report']")).ToBeVisibleAsync();
    }

    private static async Task AssertPreviousDocumentReleasedEverythingAsync(Microsoft.Playwright.IPage page)
    {
        // Recorded by the instrumentation at pagehide, after the product's own unload cleanup ran.
        var unload = await page.PreviousUnloadAsync();
        Assert.NotNull(unload);
        Assert.Equal(["ended"], unload.TrackStates);
        Assert.Contains("session.end", unload.Sent);
        Assert.NotEmpty(unload.SocketStates);
        Assert.All(unload.SocketStates, state => Assert.True(state >= 2, $"WebSocket readyState {state} is still open."));

        // The new document has not touched the microphone or opened a socket.
        var fresh = await page.SnapshotAsync();
        Assert.Equal(0, fresh.GetUserMediaCalls);
        Assert.Empty(fresh.SocketUrls);
    }

    private static Task WaitUntilEndedAsync(Microsoft.Playwright.IPage page, Guid sessionId) =>
        Eventually.HoldsAsync(() => Api.SessionStateAsync(page, sessionId), state => state == "ended", TimeSpan.FromSeconds(20), "the session to end on the server");

    [GeneratedRegex("^(conversation ended|report ready)$")]
    private static partial Regex EndedStatus();
}

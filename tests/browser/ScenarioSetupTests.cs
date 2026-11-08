using System.Text.RegularExpressions;
using AdvisorCoachStudio.BrowserTests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>Spec 18.3: all three scenarios and all difficulty selections, and microphone permission denial.</summary>
public sealed class ScenarioSetupTests(BrowserFixture browsers, SimulationAppHost host) : BrowserTest(browsers, host)
{
    [Theory]
    [InlineData("market-decline", "Worried investor after a market decline", "Alex Morgan", "Beginner")]
    [InlineData("fees-and-trust", "Skeptical client questioning fees and trust", "Jordan Lee", "Advanced")]
    [InlineData("first-time-investor", "First-time investor overwhelmed by terminology", "Taylor Reed", "Intermediate")]
    public async Task SetupDefaultsToIntermediateAndTheLivePanelShowsTheChosenDifficulty(string scenarioId, string title, string client, string difficulty)
    {
        var page = await OpenPageAsync();
        await page.GotoAsync("/");
        await page.ClickAsync($".scenario-card a[href='/practice/{scenarioId}/setup']");
        await Expect(page).ToHaveURLAsync(new Regex($"/practice/{scenarioId}/setup$"));
        await Expect(page.Locator("#setup-heading")).ToHaveTextAsync(title);
        await Expect(page.Locator("#setup")).ToContainTextAsync(client);
        await Expect(page.Locator("#readiness-text")).ToContainTextAsync("Ready");
        await Expect(page.Locator("input[name=difficulty]")).ToHaveCountAsync(3);
        await Expect(page.Locator("#difficulty-intermediate")).ToBeCheckedAsync();
        await Expect(page.Locator("#difficulty-beginner")).Not.ToBeCheckedAsync();
        await Expect(page.Locator("#difficulty-advanced")).Not.ToBeCheckedAsync();
        await Expect(page.Locator("#start-button")).ToBeDisabledAsync();
        await Expect(page.Locator("#live")).ToBeHiddenAsync();

        if (difficulty != "Intermediate")
        {
            await page.CheckAsync($"#difficulty-{difficulty.ToLowerInvariant()}");
            await Expect(page.Locator("#difficulty-intermediate")).Not.ToBeCheckedAsync();
        }

        await PracticeFlow.PrepareAsync(page);
        var sessionId = await PracticeFlow.StartAsync(page);

        await Expect(page.Locator("#setup")).ToBeHiddenAsync();
        await Expect(page.Locator("#live-heading")).ToHaveTextAsync(title);
        await Expect(page.Locator("#live-difficulty")).ToHaveTextAsync(difficulty);
        await PracticeFlow.WaitForConnectedAsync(page);

        var session = await Api.SessionAsync(page, sessionId);
        Assert.Equal(scenarioId, session.GetProperty("scenarioId").GetString());
        Assert.Equal(difficulty.ToLowerInvariant(), session.GetProperty("difficulty").GetString());
        Assert.Equal("active", session.GetProperty("state").GetString());

        await PracticeFlow.EndAsync(page);
        await Expect(page.Locator("#live-difficulty")).ToHaveTextAsync(difficulty);
    }

    [Fact]
    public async Task DeniedMicrophonePermissionExplainsTheProblemAndStartStaysDisabled()
    {
        var page = await OpenPageAsync(MicrophoneMode.PermissionDenied);
        await PracticeFlow.OpenSetupAsync(page, "market-decline");
        await page.CheckAsync("#acknowledge");
        await Expect(page.Locator("#start-button")).ToBeDisabledAsync();

        await page.ClickAsync("#mic-test");

        await Expect(page.Locator("#mic-status")).ToContainTextAsync("Microphone permission was denied");
        await Expect(page.Locator("#start-button")).ToBeDisabledAsync();
        await Expect(page.Locator("#mic-test")).ToBeEnabledAsync();
        await Expect(page.Locator("#live")).ToBeHiddenAsync();
        var probe = await page.SnapshotAsync();
        Assert.Equal(1, probe.GetUserMediaCalls);
        Assert.Equal(["NotAllowedError"], probe.GetUserMediaErrors);
        Assert.Empty(probe.TrackStates);
        Assert.Empty(probe.SocketUrls);

        // Even a programmatic submit (bypassing the disabled button) must not start a session.
        await page.EvaluateAsync("() => document.getElementById('setup-form').requestSubmit()");
        await Expect(page).ToHaveURLAsync(new Regex("/practice/market-decline/setup$"));
        await Expect(page.Locator("#live")).ToBeHiddenAsync();
        Assert.Empty((await page.SnapshotAsync()).SocketUrls);
    }
}

using System.Text.RegularExpressions;
using AdvisorCoachStudio.BrowserTests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>
/// Spec 18.3 live flow on the DevelopmentSimulation host: muting stops outbound audio, ending stops every track and
/// clears playback, and transcript partial/final updates never duplicate content.
/// </summary>
public sealed partial class LiveSessionTests(BrowserFixture browsers, SimulationAppHost host) : BrowserTest(browsers, host)
{
    [Fact]
    public async Task PracticeSessionStreamsAudioMutesEndsCleanlyAndLeadsToTheReport()
    {
        var page = await OpenPageAsync();
        await PracticeFlow.OpenSetupAsync(page, "market-decline");
        await PracticeFlow.PrepareAsync(page);
        var checkedMicrophone = await page.SnapshotAsync();
        Assert.Equal(1, checkedMicrophone.GetUserMediaCalls);
        Assert.Equal(["live"], checkedMicrophone.TrackStates);

        var sessionId = await PracticeFlow.StartAsync(page);
        await page.ClickAsync("#transcript-panel summary");
        await PracticeFlow.WaitForConnectedAsync(page);
        await Expect(page.Locator("#live-state")).ToHaveAttributeAsync("data-state", SpeakingOrListening());
        await page.WaitForAudioAppendsAboveAsync(0);

        // The client's opening line is one list item: provisional while streaming, then final (possibly marked as
        // interrupted, because the fake microphone's beeps count as advisor speech).
        var clientTurn = page.Locator("#transcript-list li.speaker-client");
        await Expect(clientTurn).ToHaveCountAsync(1);
        await Expect(clientTurn).Not.ToHaveClassAsync(Provisional(), new() { Timeout = PracticeFlow.LiveTimeoutMs });
        var clientTurnId = await clientTurn.GetAttributeAsync("data-turn-id");
        var clientText = await clientTurn.Locator("p").InnerTextAsync();
        Assert.StartsWith("[Simulated", clientText, StringComparison.Ordinal);

        var streamed = await page.SnapshotAsync();
        var history = streamed.TranscriptUpdates.Where(update => update.TurnId == clientTurnId).ToList();
        Assert.Single(streamed.TranscriptAdded, id => id == clientTurnId);
        Assert.Contains(history, update => Provisional().IsMatch(update.ClassName));
        Assert.DoesNotMatch(Provisional(), history[^1].ClassName);
        Assert.All(history, update => Assert.StartsWith(update.Text.Trim(), clientText, StringComparison.Ordinal));
        var turnIds = await page.Locator("#transcript-list li").EvaluateAllAsync<string[]>("items => items.map(item => item.dataset.turnId)");
        Assert.Equal(turnIds.Length, turnIds.Distinct().Count());

        // Mute: nothing more is sent, the capture worklet is told, and the state says so.
        await page.ClickAsync("#mute-button");
        await Expect(page.Locator("#mute-button")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(page.Locator("#mute-button")).ToHaveTextAsync("Unmute microphone");
        await Expect(page.Locator("#live-state")).ToContainTextAsync("(microphone muted)");
        var appendsWhenMuted = await page.AudioAppendsAsync();
        await page.WaitForTimeoutAsync(1500); // observation window: no audio.append may be sent while muted
        Assert.Equal(appendsWhenMuted, await page.AudioAppendsAsync());
        var muted = await page.SnapshotAsync();
        Assert.Contains("microphone.muted", muted.Sent);
        Assert.Contains("mute:true", muted.WorkletMessages("acs-capture"));

        await page.ClickAsync("#mute-button");
        await Expect(page.Locator("#mute-button")).ToHaveAttributeAsync("aria-pressed", "false");
        await Expect(page.Locator("#live-state")).Not.ToContainTextAsync("(microphone muted)");
        await page.WaitForAudioAppendsAboveAsync(appendsWhenMuted);

        // End: every captured track stops, playback is cleared, the socket closes, and the report link is offered.
        await page.ClickAsync("#end-button");
        await Expect(page.Locator("#ended-panel")).ToBeVisibleAsync(new() { Timeout = PracticeFlow.LiveTimeoutMs });
        await page.WaitForFunctionAsync("() => window.__acs.sockets.every(socket => socket.readyState === WebSocket.CLOSED)");
        var endedText = await page.InnerTextAsync("#ended-text");
        var ended = await page.SnapshotAsync();
        Assert.True(endedText.StartsWith("You ended the session.", StringComparison.Ordinal), $"Ended text: '{endedText}'. {ended}");
        Assert.Contains("session.ended", ended.Received);
        Assert.DoesNotContain(ended.Closes, close => close.Code == 1006);

        await Expect(page.Locator("#live-state")).ToHaveTextAsync("Session ended");
        await Expect(page.Locator("#mute-button")).ToBeDisabledAsync();
        await Expect(page.Locator("#end-button")).ToBeDisabledAsync();
        var reportLink = page.Locator("#report-link");
        await Expect(reportLink).ToHaveAttributeAsync("href", $"/sessions/{sessionId}/report");
        await Expect(reportLink).ToBeFocusedAsync();

        Assert.NotEmpty(ended.TrackStates);
        Assert.All(ended.TrackStates, state => Assert.Equal("ended", state));
        Assert.Contains("session.end", ended.Sent);
        var playback = ended.WorkletMessages("acs-playback");
        Assert.Contains("clear", playback);
        Assert.Equal("stop", playback[^1]);
        Assert.Equal("stop", ended.WorkletMessages("acs-capture")[^1]);
        Assert.Equal("connecting", ended.States[0]);
        Assert.Contains(ended.States, state => state is "client_speaking" or "listening");
        Assert.Equal("ended", ended.States[^1]);
        Assert.Equal(1, ended.GetUserMediaCalls);

        // Report: the simulated (or limited-evidence) report and the saved transcript with the client's turn.
        await PracticeFlow.OpenReportAsync(page, sessionId);
        await PracticeFlow.WaitForReportStatusAsync(page, "ready");
        await Expect(page.Locator("#report-root .alert-warning")).ToContainTextAsync("Simulated development data - not Azure");
        await Expect(page.Locator("#overall-heading")).ToBeVisibleAsync();
        var hasOverallScore = await page.Locator(".score-big").CountAsync() == 1;
        var hasLimitedEvidence = await page.Locator(".overall strong", new() { HasText = "Limited evidence" }).CountAsync() == 1;
        Assert.True(hasOverallScore ^ hasLimitedEvidence, "The report shows either an overall score or the limited-evidence state.");
        await Expect(page.Locator(".skill")).ToHaveCountAsync(6);
        var savedClientTurn = page.Locator("#report-transcript li.turn-client").First;
        await Expect(savedClientTurn).ToContainTextAsync(clientText);
        await Expect(page.Locator("#report-transcript li")).Not.ToHaveCountAsync(0);
        Assert.Empty(PageErrors);
    }

    [GeneratedRegex(@"\bprovisional\b")]
    private static partial Regex Provisional();

    [GeneratedRegex("^(client_speaking|interrupted|listening)$")]
    private static partial Regex SpeakingOrListening();
}

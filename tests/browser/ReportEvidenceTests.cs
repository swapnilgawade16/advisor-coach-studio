using System.Text.Json;
using System.Text.RegularExpressions;
using AdvisorCoachStudio.BrowserTests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>Spec 18.3: limited-evidence and ready report states (Azure-mode host with a scripted report model).</summary>
public sealed partial class ReportEvidenceTests(BrowserFixture browsers, AzureAppHost host) : AzureReportTest(browsers, host)
{
    [Fact]
    public async Task NoAdvisorTurnsGiveLimitedEvidenceWithoutAnOverallScore()
    {
        var callsBefore = Reports.CallCount;
        var page = await OpenPageAsync(MicrophoneMode.Silence);
        var sessionId = await PracticeFlow.StartSessionAsync(page, "first-time-investor");
        await PracticeFlow.WaitForListeningAsync(page);
        await PracticeFlow.EndAsync(page);
        await PracticeFlow.OpenReportAsync(page, sessionId);
        await PracticeFlow.WaitForReportStatusAsync(page, "ready");

        await Expect(page.Locator(".overall strong")).ToHaveTextAsync("Limited evidence");
        await Expect(page.Locator(".score-big")).ToHaveCountAsync(0);
        await Expect(page.Locator("#overall-heading ~ p.hint")).ToContainTextAsync("This attempt had 0 scored skill(s) and 0 usable turn(s).");
        await Expect(page.Locator(".skill .score-pill")).ToHaveCountAsync(6);
        await Expect(page.Locator(".skill .score-pill[data-score='none']")).ToHaveCountAsync(6);
        await Expect(page.Locator("a.evidence-link")).ToHaveCountAsync(0);
        await Expect(page.GetByText("There was not enough of your side of the conversation")).ToBeVisibleAsync();
        await Expect(page.Locator("#report-root .alert-warning")).ToHaveCountAsync(0);

        // The transcript holds the client's complete opening line and no advisor turn; the model was never asked.
        await Expect(page.Locator("#report-transcript li.turn-advisor")).ToHaveCountAsync(0);
        await Expect(page.Locator("#report-transcript li.turn-client")).ToHaveCountAsync(1);
        await Expect(page.Locator("#report-transcript li.turn-client .tag-warn")).ToHaveCountAsync(0);
        Assert.Equal(callsBefore, Reports.CallCount);
    }

    [Fact]
    public async Task ReadyReportShowsSkillScoresAndEvidenceLinksRevealTheCitedTurn()
    {
        var (page, sessionId) = await PractiseWithAdvisorTurnsAsync("market-decline", advisorTurns: 3);
        var call = await Reports.NextCallAsync(ModelCallTimeout);
        Assert.True(call.Turns.Count(turn => turn.Speaker == "advisor" && turn.Quotable) >= 3);
        call.Succeed();
        await PracticeFlow.OpenReportAsync(page, sessionId);
        await PracticeFlow.WaitForReportStatusAsync(page, "ready");

        // Scores: four scored skills (4, 3, 4, 3) and two without evidence; the server computes the 3.5 average.
        await Expect(page.Locator(".score-big")).ToHaveTextAsync("3.5");
        await Expect(page.Locator("#report-root .alert-warning")).ToHaveCountAsync(0);
        Assert.Equal(["4 / 5", "3 / 5", "4 / 5", "3 / 5", "Not scored", "Not scored"], await page.Locator(".skill .score-pill").AllInnerTextsAsync());
        await Expect(page.GetByText(ScriptedReportContent.StrengthTitle)).ToBeVisibleAsync();
        await Expect(page.GetByText(ScriptedReportContent.ImprovementTitle)).ToBeVisibleAsync();

        // Evidence links: the transcript starts collapsed; following a link expands it and focuses the cited turn.
        Assert.False(await IsTranscriptOpenAsync(page));
        var links = page.Locator(".skill a.evidence-link");
        await Expect(links).ToHaveCountAsync(ScriptedReportContent.ScoredCategories);
        var first = links.First;
        var label = await first.InnerTextAsync();
        Assert.Matches(TurnLabel(), label);
        var quote = await first.Locator("xpath=following-sibling::q").InnerTextAsync();
        await first.ClickAsync();

        var cited = page.Locator($"#turn-{label}");
        Assert.True(await IsTranscriptOpenAsync(page));
        await Expect(cited).ToBeFocusedAsync();
        await Expect(cited).ToHaveClassAsync(new Regex(@"\bhighlight\b"));
        await Expect(cited).ToBeInViewportAsync();
        await Expect(cited).ToContainTextAsync("You (advisor)");
        await Expect(cited.Locator("p")).ToContainTextAsync(quote);
        await Expect(page).ToHaveURLAsync(new Regex($"#turn-{label}$"));

        // A different citation moves the focus and the highlight.
        var second = links.Nth(1);
        var secondLabel = await second.InnerTextAsync();
        Assert.NotEqual(label, secondLabel);
        await second.ClickAsync();
        await Expect(page.Locator($"#turn-{secondLabel}")).ToBeFocusedAsync();
        await Expect(page.Locator("#report-transcript li.highlight")).ToHaveCountAsync(1);

        // The fully populated report also fits a 375 px wide screen.
        await page.SetViewportSizeAsync(BrowserTest.Phone.Width, BrowserTest.Phone.Height);
        var widths = await page.EvaluateAsync<JsonElement>("() => [document.documentElement.scrollWidth, window.innerWidth]");
        Assert.True(widths[0].GetInt32() <= widths[1].GetInt32(), $"Report scrolls horizontally at 375 px: {widths}");
        Assert.Empty(PageErrors);
    }

    private static Task<bool> IsTranscriptOpenAsync(Microsoft.Playwright.IPage page) =>
        page.EvaluateAsync<bool>("() => document.getElementById('report-transcript').open");

    [GeneratedRegex("^T[0-9]+$")]
    private static partial Regex TurnLabel();
}

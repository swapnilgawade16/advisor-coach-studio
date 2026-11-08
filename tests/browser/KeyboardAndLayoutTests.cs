using System.Text.Json;
using AdvisorCoachStudio.BrowserTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>Spec 18.3 (and 4.7): keyboard controls with a visible focus indicator, and narrow-screen layout.</summary>
public sealed class KeyboardAndLayoutTests(BrowserFixture browsers, SimulationAppHost host) : BrowserTest(browsers, host)
{
    private const string FocusScript = """
        () => {
          const element = document.activeElement;
          const style = getComputedStyle(element);
          return { id: element.id, focusVisible: element.matches(':focus-visible'), outlineStyle: style.outlineStyle, outlineWidth: style.outlineWidth };
        }
        """;

    private const string OverflowScript = """
        () => ({
          scrollWidth: document.documentElement.scrollWidth,
          innerWidth: window.innerWidth,
          offenders: [...document.querySelectorAll('body *')]
            .filter(element => element.getClientRects().length > 0 && element.getBoundingClientRect().right > window.innerWidth + 0.5)
            .slice(0, 5)
            .map(element => `${element.tagName.toLowerCase()}${element.id ? '#' + element.id : ''}.${[...element.classList].join('.')} right=${Math.round(element.getBoundingClientRect().right)}`),
        })
        """;

    [Fact]
    public async Task SetupAndLiveControlsWorkFromTheKeyboardWithAVisibleFocusIndicator()
    {
        var page = await OpenPageAsync();
        await PracticeFlow.OpenSetupAsync(page, "first-time-investor");

        // Tab from the top of the page: the difficulty group (its checked radio), the microphone check, and the
        // acknowledgement are all reachable, and the disabled Start button is skipped.
        var reached = await TabUntilAsync(page, "acknowledge");
        var ids = reached.Select(focus => focus.Id).ToList();
        Assert.True(ids.IndexOf("difficulty-intermediate") < ids.IndexOf("mic-test"), string.Join(", ", ids));
        Assert.True(ids.IndexOf("mic-test") < ids.IndexOf("acknowledge"), string.Join(", ", ids));
        Assert.DoesNotContain("start-button", ids);
        Assert.Single(ids, id => id.StartsWith("difficulty-", StringComparison.Ordinal));
        foreach (var focus in reached.Where(focus => focus.Id is "difficulty-intermediate" or "mic-test" or "acknowledge"))
        {
            AssertVisibleFocus(focus);
        }

        // Operate everything with the keyboard only.
        await page.Keyboard.PressAsync("Space");
        await Expect(page.Locator("#acknowledge")).ToBeCheckedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Expect(page.Locator("#mic-test")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#mic-status")).ToContainTextAsync("Microphone is working");
        await page.Keyboard.PressAsync("Shift+Tab");
        await Expect(page.Locator("#difficulty-intermediate")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowDown");
        await Expect(page.Locator("#difficulty-advanced")).ToBeCheckedAsync();
        await Expect(page.Locator("#difficulty-advanced")).ToBeFocusedAsync();
        AssertVisibleFocus(await FocusAsync(page));

        var start = await TabUntilAsync(page, "start-button");
        AssertVisibleFocus(start[^1]);
        await page.Keyboard.PressAsync("Enter");
        await Expect(page).ToHaveURLAsync(PracticeFlow.PracticeUrl());
        await Expect(page.Locator("#live-difficulty")).ToHaveTextAsync("Advanced");
        await PracticeFlow.WaitForConnectedAsync(page);

        // The Start button disappears with the setup form, so focus moves to the live conversation heading and the
        // next Tab reaches the live controls.
        await Expect(page.Locator("#live-heading")).ToBeFocusedAsync();
        var mute = await TabUntilAsync(page, "mute-button");
        Assert.Single(mute);
        AssertVisibleFocus(mute[^1]);
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#mute-button")).ToHaveAttributeAsync("aria-pressed", "true");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("#end-button")).ToBeFocusedAsync();
        AssertVisibleFocus(await FocusAsync(page));
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#ended-panel")).ToBeVisibleAsync(new() { Timeout = PracticeFlow.LiveTimeoutMs });
        await Expect(page.Locator("#report-link")).ToBeFocusedAsync();
        AssertVisibleFocus(await FocusAsync(page));
    }

    [Fact]
    public async Task PagesDoNotScrollHorizontallyOnA375PixelWideScreen()
    {
        var page = await OpenPageAsync(viewport: Phone);
        await page.GotoAsync("/");
        await Expect(page.Locator(".scenario-card")).ToHaveCountAsync(3);
        await AssertNoHorizontalScrollAsync(page, "scenario list");

        await PracticeFlow.OpenSetupAsync(page, "fees-and-trust");
        await AssertNoHorizontalScrollAsync(page, "setup");

        await PracticeFlow.PrepareAsync(page);
        var sessionId = await PracticeFlow.StartAsync(page);
        await PracticeFlow.WaitForConnectedAsync(page);
        await page.ClickAsync("#transcript-panel summary");
        await PracticeFlow.WaitForListeningAsync(page);
        await AssertNoHorizontalScrollAsync(page, "live conversation");

        await PracticeFlow.EndAsync(page);
        await PracticeFlow.OpenReportAsync(page, sessionId);
        await PracticeFlow.WaitForReportStatusAsync(page, "ready");
        await AssertNoHorizontalScrollAsync(page, "report");
        await page.ClickAsync("#report-transcript summary");
        await Expect(page.Locator("#report-transcript li").First).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(page, "report with transcript");
    }

    [Fact]
    public async Task PublicAndSetupPagesReflowAt320CssPixels()
    {
        var page = await OpenPageAsync(viewport: Reflow);
        await page.GotoAsync("/");
        await Expect(page.Locator(".scenario-card")).ToHaveCountAsync(3);
        await AssertNoHorizontalScrollAsync(page, "scenario list at 320px");

        foreach (var scenarioId in new[] { "market-decline", "fees-and-trust", "first-time-investor" })
        {
            await PracticeFlow.OpenSetupAsync(page, scenarioId);
            await AssertNoHorizontalScrollAsync(page, $"setup {scenarioId} at 320px");

            // Users with a larger default text size (125%) must not get horizontal scrolling either.
            await page.EvaluateAsync("() => { document.documentElement.style.fontSize = '20px'; }");
            await AssertNoHorizontalScrollAsync(page, $"setup {scenarioId} at 320px with 125% text");
        }
    }

    private static async Task<List<FocusState>> TabUntilAsync(IPage page, string targetId)
    {
        var reached = new List<FocusState>();
        for (var i = 0; i < 40; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            var focus = await FocusAsync(page);
            reached.Add(focus);
            if (focus.Id == targetId)
            {
                return reached;
            }
        }

        throw new InvalidOperationException($"#{targetId} was not reached by Tab. Focus order: {string.Join(", ", reached.Select(f => f.Id))}");
    }

    private static async Task<FocusState> FocusAsync(IPage page) =>
        (await page.EvaluateAsync<JsonElement>(FocusScript)).Deserialize<FocusState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static void AssertVisibleFocus(FocusState focus)
    {
        Assert.True(focus.FocusVisible, $"#{focus.Id} does not match :focus-visible.");
        Assert.NotEqual("none", focus.OutlineStyle);
        Assert.NotEqual("0px", focus.OutlineWidth);
    }

    private static async Task AssertNoHorizontalScrollAsync(IPage page, string name)
    {
        var layout = await page.EvaluateAsync<JsonElement>(OverflowScript);
        var scrollWidth = layout.GetProperty("scrollWidth").GetInt32();
        var innerWidth = layout.GetProperty("innerWidth").GetInt32();
        Assert.True(scrollWidth <= innerWidth, $"{name}: scrollWidth {scrollWidth} > innerWidth {innerWidth}. Widest: {layout.GetProperty("offenders")}");
    }

    private sealed record FocusState(string Id, bool FocusVisible, string OutlineStyle, string OutlineWidth);
}

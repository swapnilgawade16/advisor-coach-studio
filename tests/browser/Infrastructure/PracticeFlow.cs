using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>User-level steps shared by the tests. Every wait is on visible UI state, never a fixed sleep.</summary>
public static partial class PracticeFlow
{
    public const float LiveTimeoutMs = 30_000;

    public static async Task OpenSetupAsync(IPage page, string scenarioId)
    {
        await page.GotoAsync($"/practice/{scenarioId}/setup");
        await Expect(page.Locator("#setup-form")).ToBeVisibleAsync();
    }

    /// <summary>Runs the microphone check and acknowledges the notice, after which Start is enabled.</summary>
    public static async Task PrepareAsync(IPage page)
    {
        await page.ClickAsync("#mic-test");
        await Expect(page.Locator("#mic-status")).ToContainTextAsync("Microphone is working");
        await page.CheckAsync("#acknowledge");
        await Expect(page.Locator("#start-button")).ToBeEnabledAsync();
    }

    /// <summary>Clicks Start and returns the session id once the address bar shows /practice/{id}.</summary>
    public static async Task<Guid> StartAsync(IPage page)
    {
        await page.ClickAsync("#start-button");
        await Expect(page).ToHaveURLAsync(PracticeUrl());
        await Expect(page.Locator("#live")).ToBeVisibleAsync();
        return Guid.Parse(PracticeUrl().Match(page.Url).Groups["id"].Value);
    }

    /// <summary>Setup, microphone check, Start, and wait until the session is ready (End enabled).</summary>
    public static async Task<Guid> StartSessionAsync(IPage page, string scenarioId = "market-decline", string? difficulty = null)
    {
        await OpenSetupAsync(page, scenarioId);
        if (difficulty is not null)
        {
            await page.CheckAsync($"#difficulty-{difficulty.ToLowerInvariant()}");
        }

        await PrepareAsync(page);
        var sessionId = await StartAsync(page);
        await WaitForConnectedAsync(page);
        return sessionId;
    }

    /// <summary>session.ready has arrived: the live controls are enabled.</summary>
    public static Task WaitForConnectedAsync(IPage page) =>
        Expect(page.Locator("#end-button")).ToBeEnabledAsync(new() { Timeout = LiveTimeoutMs });

    /// <summary>The client finished speaking and the session is listening to the advisor.</summary>
    public static Task WaitForListeningAsync(IPage page) =>
        Expect(page.Locator("#live-state")).ToHaveAttributeAsync("data-state", "listening", new() { Timeout = LiveTimeoutMs });

    /// <summary>Clicks End and waits for the ended panel.</summary>
    public static async Task EndAsync(IPage page)
    {
        await page.ClickAsync("#end-button");
        await Expect(page.Locator("#ended-panel")).ToBeVisibleAsync(new() { Timeout = LiveTimeoutMs });
    }

    /// <summary>Follows the ended panel's report link.</summary>
    public static async Task OpenReportAsync(IPage page, Guid sessionId)
    {
        await page.ClickAsync("#report-link");
        await Expect(page).ToHaveURLAsync(new Regex($"/sessions/{sessionId}/report$"));
    }

    /// <summary>The report page polls and reloads itself when the status changes; wait for the rendered status.</summary>
    public static Task WaitForReportStatusAsync(IPage page, string status, float timeoutMs = LiveTimeoutMs) =>
        Expect(page.Locator("#report-root")).ToHaveAttributeAsync("data-report-status", status, new() { Timeout = timeoutMs });

    /// <summary>Waits until the live transcript shows at least <paramref name="count"/> final advisor turns.</summary>
    public static Task WaitForAdvisorTurnsAsync(IPage page, int count, float timeoutMs = 45_000) =>
        page.WaitForFunctionAsync(
            "count => document.querySelectorAll('#transcript-list li.speaker-advisor:not(.provisional)').length >= count",
            count,
            new PageWaitForFunctionOptions { Timeout = timeoutMs });

    [GeneratedRegex("/practice/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    public static partial Regex PracticeUrl();
}

/// <summary>Application API reads made with the page's own cookies.</summary>
public static class Api
{
    public static async Task<JsonElement> GetAsync(IPage page, string path)
    {
        var response = await page.APIRequest.GetAsync(path);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"GET {path} returned {response.Status}: {await response.TextAsync()}");
        }

        return await response.JsonAsync() ?? throw new InvalidOperationException($"GET {path} returned no JSON.");
    }

    public static async Task<JsonElement> SessionAsync(IPage page, Guid sessionId) => await GetAsync(page, $"/api/sessions/{sessionId}");

    public static async Task<string> SessionStateAsync(IPage page, Guid sessionId) =>
        (await SessionAsync(page, sessionId)).GetProperty("state").GetString() ?? "";
}

/// <summary>Polls server-side state (which has no UI event to wait on) until a condition holds.</summary>
public static class Eventually
{
    public static async Task<T> HoldsAsync<T>(Func<Task<T>> read, Func<T, bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var value = await read();
            if (condition(value))
            {
                return value;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {description}. Last value: {value}");
            }

            await Task.Delay(100);
        }
    }
}

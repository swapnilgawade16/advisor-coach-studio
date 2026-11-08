using System.Text.RegularExpressions;
using AdvisorCoachStudio.BrowserTests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>Spec 18.3: access page through to the scenario list, and sign-out.</summary>
public sealed partial class AccessTests(BrowserFixture browsers, SimulationAppHost host) : BrowserTest(browsers, host)
{
    [Fact]
    public async Task WrongCodeShowsGenericErrorRightCodeListsScenariosAndSignOutReturnsToAccess()
    {
        var page = await OpenPageAsync(signedIn: false);

        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(AccessUrl());
        await Expect(page.Locator("#access-heading")).ToBeVisibleAsync();
        await Expect(page.Locator(".scenario-card")).ToHaveCountAsync(0);

        await page.FillAsync("#code", "definitely-not-the-code");
        await page.ClickAsync("form button[type=submit]");
        var error = page.Locator("#access-error");
        await Expect(error).ToHaveTextAsync("That access code is not valid.");
        await Expect(error).ToHaveAttributeAsync("role", "alert");
        await Expect(page.Locator("#code")).ToHaveValueAsync("");
        await Expect(page.Locator("#code")).ToHaveAttributeAsync("aria-describedby", "code-help access-error");
        await Expect(page.Locator(".scenario-card")).ToHaveCountAsync(0);

        await page.FillAsync("#code", AppHost.AccessCode);
        await page.ClickAsync("form button[type=submit]");
        await Expect(page).ToHaveURLAsync(RootUrl());
        await Expect(page.Locator("#scenarios-heading")).ToBeVisibleAsync();
        var cards = page.Locator(".scenario-card");
        await Expect(cards).ToHaveCountAsync(3);
        var titles = await cards.Locator("h3").AllInnerTextsAsync();
        Assert.Equal(
            ["First-time investor overwhelmed by terminology", "Skeptical client questioning fees and trust", "Worried investor after a market decline"],
            titles.Order(StringComparer.Ordinal));
        foreach (var scenarioId in new[] { "market-decline", "fees-and-trust", "first-time-investor" })
        {
            await Expect(page.Locator($".scenario-card a[href='/practice/{scenarioId}/setup']")).ToBeVisibleAsync();
        }

        await page.ClickAsync("header button:has-text('Sign out')");
        await Expect(page).ToHaveURLAsync(AccessUrl());
        await Expect(page.Locator("#access-heading")).ToBeVisibleAsync();
        await Expect(page.Locator("header button:has-text('Sign out')")).ToHaveCountAsync(0);

        // The access cookie is gone: the scenario list is protected again.
        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(AccessUrl());
    }

    [GeneratedRegex(@"/access(\?.*)?$")]
    private static partial Regex AccessUrl();

    [GeneratedRegex(@"^http://127\.0\.0\.1:\d+/$")]
    private static partial Regex RootUrl();
}

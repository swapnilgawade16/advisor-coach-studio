using System.Text.Json;
using AdvisorCoachStudio.IntegrationTests.Infrastructure;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Features.Sessions;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Access gate, CSRF, logout/revocation, health, caching, and public-catalogue projection (spec 14, 15).</summary>
public sealed class AccessAndHttpSecurityTests
{
    [Fact]
    public async Task WrongAccessCodeIsRejectedWithoutEchoingTheCode()
    {
        await using var app = await TestApp.StartAsync();
        using var user = app.CreateUser();
        var anonymousToken = await user.FetchCsrfTokenAsync("/access");
        const string wrongCode = "wrong-code-7f3a91";

        var response = await user.PostAsync("/api/access/validate", new { code = wrongCode }, anonymousToken);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("invalid_access_code", response.ProblemCode);
        Assert.DoesNotContain(wrongCode, response.Body, StringComparison.Ordinal);
        Assert.True(response.IsNoStore);
        Assert.DoesNotContain("acs_access", user.CookieHeader, StringComparison.Ordinal);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(wrongCode, StringComparison.Ordinal));
        Assert.Equal(401, (await user.GetAsync("/api/scenarios")).StatusCode);
    }

    [Theory]
    [InlineData("/api/scenarios")]
    [InlineData("/api/capabilities")]
    [InlineData("/api/scenarios/market-decline")]
    [InlineData("/api/sessions/7d6f3c1e-2a4b-4c8d-9e0f-1a2b3c4d5e6f")]
    public async Task ApiRequiresAnAccessSession(string path)
    {
        await using var app = await TestApp.StartAsync();
        using var user = app.CreateUser();

        var response = await user.GetAsync(path);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("access_required", response.ProblemCode);
    }

    [Fact]
    public async Task StateChangingRequestsRequireTheAntiforgeryHeader()
    {
        await using var app = await TestApp.StartAsync();
        using var anonymous = app.CreateUser();
        await anonymous.FetchCsrfTokenAsync("/access");
        var loginWithoutToken = await anonymous.PostAsync("/api/access/validate", new { code = TestAppFactory.AccessCode }, csrfToken: null);
        Assert.Equal(400, loginWithoutToken.StatusCode);
        Assert.Equal("csrf_validation_failed", loginWithoutToken.ProblemCode);

        using var user = app.CreateUser();
        var anonymousToken = await user.FetchCsrfTokenAsync("/access");
        await user.LoginAsync();
        var body = new { scenarioId = "market-decline", difficulty = "intermediate", acknowledgedNotice = true };

        var missing = await user.PostAsync("/api/sessions", body, csrfToken: null);
        var forged = await user.PostAsync("/api/sessions", body, "not-a-real-token");
        var wrongIdentity = await user.PostAsync("/api/sessions", body, anonymousToken);

        foreach (var rejected in new[] { missing, forged, wrongIdentity })
        {
            Assert.Equal(400, rejected.StatusCode);
            Assert.Equal("csrf_validation_failed", rejected.ProblemCode);
        }

        Assert.Equal(0, app.Service<PracticeSessionStore>().Count);
        var accepted = await user.PostAsync("/api/sessions", body);
        Assert.Equal(201, accepted.StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesTheAccessSessionAndDiscardsItsData()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        var sessionId = await user.CreateSessionAsync();
        var copiedCookies = user.CookieHeader;

        var logout = await user.PostAsync("/api/access/logout");

        Assert.Equal(204, logout.StatusCode);
        Assert.Equal(401, (await user.GetAsync("/api/scenarios")).StatusCode);
        Assert.Null(app.Service<PracticeSessionStore>().Get(sessionId));

        // Replaying the old cookie must not work: sign-out revokes the owner server-side.
        using var replay = app.Factory.CreateDefaultClient(new Uri(TestUser.BaseAddress));
        replay.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", copiedCookies);
        using var replayed = await replay.GetAsync("/api/scenarios", TestContext.Current.CancellationToken);
        Assert.Equal(401, (int)replayed.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpointsAreAnonymous(string path)
    {
        await using var app = await TestApp.StartAsync(new TestAppOptions { RealtimeConfigured = false, ReportsConfigured = false });
        using var user = app.CreateUser();

        var response = await user.GetAsync(path);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Healthy", response.Body);
    }

    [Fact]
    public async Task SessionApiResponsesAreNeverCacheable()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();

        var created = await user.PostAsync("/api/sessions", new { scenarioId = "market-decline", difficulty = "beginner", acknowledgedNotice = true });
        var sessionId = created.Json.GetProperty("sessionId").GetGuid();
        var responses = new[]
        {
            created,
            await user.GetSessionAsync(sessionId),
            await user.GetTranscriptAsync(sessionId),
            await user.GetReportAsync(sessionId),
            await user.GetSessionAsync(Guid.NewGuid()),
            await user.DeleteAsync($"/api/sessions/{sessionId}"),
        };

        Assert.Equal(new[] { 201, 200, 200, 200, 404, 204 }, responses.Select(r => r.StatusCode));
        Assert.All(responses, response => Assert.True(response.IsNoStore, $"Expected Cache-Control: no-store on {response}"));
    }

    [Fact]
    public async Task ScenarioCatalogNeverExposesPrivateScenarioContent()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        var catalog = app.Service<ScenarioCatalog>();
        var publicTexts = PublicTexts(catalog);

        var list = await user.GetAsync("/api/scenarios");
        var single = await user.GetAsync("/api/scenarios/market-decline");

        Assert.Equal(200, list.StatusCode);
        Assert.Equal(200, single.StatusCode);
        Assert.Equal(new[] { "market-decline", "fees-and-trust", "first-time-investor" }, list.Json.EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal("Alex Morgan", single.String("clientDisplayName"));

        foreach (var response in new[] { list, single })
        {
            var visible = string.Join("\n", AllStrings(response.Json));
            Assert.DoesNotContain("partner", visible, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("five years", visible, StringComparison.OrdinalIgnoreCase);
            foreach (var name in new[] { "hiddenFacts", "openingUtterance", "behaviorGuidance", "clientProfile", "knownFacts", "difficultyGuidance", "coachingFocus" })
            {
                Assert.DoesNotContain(name, visible, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var scenario in catalog.All)
            {
                foreach (var secret in PrivateTexts(scenario).Except(publicTexts, StringComparer.Ordinal))
                {
                    Assert.DoesNotContain(secret, visible, StringComparison.Ordinal);
                }
            }
        }
    }

    private static IEnumerable<string> PrivateTexts(ScenarioDefinition scenario) =>
        scenario.HiddenFacts.SelectMany(f => new[] { f.Fact, f.DiscloseWhen })
            .Concat([scenario.OpeningUtterance, scenario.ClientProfile, scenario.InitialEmotionalState])
            .Concat(scenario.KnownFacts).Concat(scenario.Goals).Concat(scenario.Concerns)
            .Concat(scenario.BehaviorGuidance).Concat(scenario.SafetyBoundaries).Concat(scenario.CoachingFocus)
            .Concat([scenario.DifficultyGuidance.Beginner, scenario.DifficultyGuidance.Intermediate, scenario.DifficultyGuidance.Advanced]);

    /// <summary>Strings the public projection is allowed to contain (some coaching-focus labels double as public skill focus).</summary>
    private static HashSet<string> PublicTexts(ScenarioCatalog catalog) =>
        catalog.All.SelectMany(s => s.SkillFocus.Concat([s.Title, s.PublicBrief, s.ClientDisplayName])).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every decoded property name and string value (so JSON escaping cannot hide a leak).</summary>
    private static IEnumerable<string> AllStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var value in AllStrings(property.Value))
                    {
                        yield return value;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in AllStrings(item))
                    {
                        yield return value;
                    }
                }

                break;
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
        }
    }
}

using AdvisorCoachStudio.IntegrationTests.Infrastructure;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Session IDs are not authorization: another owner's session is indistinguishable from a missing one (spec 14.1).</summary>
public sealed class OwnershipTests
{
    public static TheoryData<string, string> SessionRoutes => new()
    {
        { "GET", "" },
        { "GET", "/transcript" },
        { "GET", "/report" },
        { "POST", "/complete" },
        { "POST", "/report/retry" },
        { "POST", "/demonstrations" },
        { "DELETE", "" },
    };

    [Theory]
    [MemberData(nameof(SessionRoutes))]
    public async Task ForeignSessionReturnsTheSameNotFoundShapeAsAMissingSession(string method, string suffix)
    {
        await using var app = await TestApp.StartAsync();
        using var alice = await app.LoginAsync();
        using var bob = await app.LoginAsync();
        var aliceSession = await alice.CreateSessionAsync();

        var foreign = await bob.SendAsync(new HttpMethod(method), $"/api/sessions/{aliceSession}{suffix}", null, bob.CsrfToken);
        var missing = await bob.SendAsync(new HttpMethod(method), $"/api/sessions/{Guid.NewGuid()}{suffix}", null, bob.CsrfToken);

        Assert.Equal(404, foreign.StatusCode);
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal("not_found", foreign.ProblemCode);
        Assert.Equal(missing.NormalizedProblem(), foreign.NormalizedProblem());
        Assert.Equal(missing.ContentType, foreign.ContentType);
        Assert.DoesNotContain("market-decline", foreign.Body, StringComparison.Ordinal);

        // Alice's session is untouched by Bob's attempts.
        var own = await alice.GetSessionAsync(aliceSession);
        Assert.Equal(200, own.StatusCode);
        Assert.Equal("created", own.String("state"));
    }

    [Fact]
    public async Task ForeignSessionSocketIsRejectedBeforeAnyProviderConnection()
    {
        await using var app = await TestApp.StartAsync();
        using var alice = await app.LoginAsync();
        using var bob = await app.LoginAsync();
        var aliceSession = await alice.CreateSessionAsync();

        var foreign = await bob.ConnectExpectingRejectionAsync($"/ws/sessions/{aliceSession}");
        var missing = await bob.ConnectExpectingRejectionAsync($"/ws/sessions/{Guid.NewGuid()}");

        Assert.Equal(404, foreign.Status);
        Assert.Equal("not_found", foreign.Code);
        Assert.Equal(missing.Status, foreign.Status);
        Assert.Equal(missing.Code, foreign.Code);
        Assert.Equal(0, app.Fake.UpgradeAttempts);
    }
}

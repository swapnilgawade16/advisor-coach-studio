using AdvisorCoachStudio.IntegrationTests.Infrastructure;
using AdvisorCoachStudio.Web.Features.Sessions;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Handshake validation happens before the upgrade and before any provider connection (spec 7 step 9, 15.2).</summary>
public sealed class WebSocketHandshakeTests
{
    [Fact]
    public async Task InvalidHandshakesAreRejectedWithoutContactingTheProvider()
    {
        await using var app = await TestApp.StartAsync();
        using var alice = await app.LoginAsync();
        using var bob = await app.LoginAsync();
        using var anonymous = app.CreateUser();
        var sessionId = await alice.CreateSessionAsync();
        var path = $"/ws/sessions/{sessionId}";

        var noCookie = await anonymous.ConnectExpectingRejectionAsync(path);
        var noOrigin = await alice.ConnectExpectingRejectionAsync(path, origin: null);
        var foreignOrigin = await alice.ConnectExpectingRejectionAsync(path, origin: "https://evil.example");
        var downgradedOrigin = await alice.ConnectExpectingRejectionAsync(path, origin: "http://localhost");
        var noSubprotocol = await alice.ConnectExpectingRejectionAsync(path, subprotocol: null);
        var wrongSubprotocol = await alice.ConnectExpectingRejectionAsync(path, subprotocol: "acs.v2");
        var otherOwner = await bob.ConnectExpectingRejectionAsync(path);
        var unknownSession = await alice.ConnectExpectingRejectionAsync($"/ws/sessions/{Guid.NewGuid()}");
        var plainHttp = await alice.GetAsync(path);

        Assert.Equal((401, "access_required"), (noCookie.Status, noCookie.Code));
        Assert.Equal((403, "origin_not_allowed"), (noOrigin.Status, noOrigin.Code));
        Assert.Equal((403, "origin_not_allowed"), (foreignOrigin.Status, foreignOrigin.Code));
        Assert.Equal((403, "origin_not_allowed"), (downgradedOrigin.Status, downgradedOrigin.Code));
        Assert.Equal((400, "unsupported_subprotocol"), (noSubprotocol.Status, noSubprotocol.Code));
        Assert.Equal((400, "unsupported_subprotocol"), (wrongSubprotocol.Status, wrongSubprotocol.Code));
        Assert.Equal((404, "not_found"), (otherOwner.Status, otherOwner.Code));
        Assert.Equal((404, "not_found"), (unknownSession.Status, unknownSession.Code));
        Assert.Equal((400, "websocket_required"), (plainHttp.StatusCode, plainHttp.ProblemCode));

        Assert.Equal(0, app.Fake.UpgradeAttempts);
        Assert.Equal(0, app.Factory.Connector.ConnectCalls);
        Assert.Equal("created", (await alice.GetSessionAsync(sessionId)).String("state"));

        // The untouched session still starts normally with a valid browser handshake.
        await using var socket = await alice.ConnectAsync(path);
        await socket.WaitForAsync("session.ready");
        Assert.Equal(TestUser.Subprotocol, socket.SubProtocol);
        Assert.Equal(1, app.Fake.UpgradeAttempts);
    }

    [Fact]
    public async Task SecondSocketForALiveSessionIsRejected()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);

        var second = await user.ConnectExpectingRejectionAsync($"/ws/sessions/{roleplay.SessionId}");

        Assert.Equal(409, second.Status);
        Assert.Equal("session_not_startable", second.Code);
        Assert.Single(app.Fake.Connections);

        // The first connection is unaffected and can still finish normally.
        await roleplay.HearAsync(Scripts.Opening);
        var ended = await roleplay.EndAsync();
        Assert.Equal("user_ended", ended.String("reason"));
    }

    [Fact]
    public async Task SessionCreationReportsMissingRealtimeConfiguration()
    {
        await using var app = await TestApp.StartAsync(new TestAppOptions { RealtimeConfigured = false, ReportsConfigured = false });
        using var user = await app.LoginAsync();

        var capabilities = await user.GetAsync("/api/capabilities");
        var created = await user.PostAsync("/api/sessions", new { scenarioId = "market-decline", difficulty = "intermediate", acknowledgedNotice = true });

        Assert.Equal("setup_required", capabilities.Json.GetProperty("realtime").GetProperty("status").GetString());
        Assert.Equal("setup_required", capabilities.Json.GetProperty("reports").GetProperty("status").GetString());
        Assert.False(capabilities.Json.GetProperty("demonstrationAvailable").GetBoolean());
        Assert.Equal(503, created.StatusCode);
        Assert.Equal("provider_not_configured", created.ProblemCode);
        Assert.DoesNotContain(TestAppFactory.FakeEndpoint, capabilities.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, app.Service<PracticeSessionStore>().Count);
    }

    [Fact]
    public async Task ProviderSetupFailureFailsTheAttemptAndReleasesResources()
    {
        await using var app = await TestApp.StartAsync();
        app.Fake.Behavior.SessionUpdateError = ("authentication_error", "invalid_api_key");
        using var user = await app.LoginAsync();
        var sessionId = await user.CreateSessionAsync();
        await using var socket = await user.ConnectAsync($"/ws/sessions/{sessionId}");

        var error = await socket.WaitForAsync("error");
        await socket.WaitForStateAsync("ended");
        await socket.WaitForClosedAsync();
        var provider = await app.Fake.WaitForConnectionAsync(0);
        await provider.WaitForClosedAsync();
        await app.WaitForLiveConnectionsReleasedAsync();

        Assert.Equal("provider_auth_failed", error.String("code"));
        Assert.False(error.Bool("retryable"));
        Assert.False(string.IsNullOrEmpty(error.String("correlationId")));
        Assert.DoesNotContain("invalid_api_key", error.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain(socket.Messages, m => m.Type is "session.ready" or "audio.delta");
        Assert.Empty(provider.ReceivedOfType("response.create"));
        Assert.Single(provider.ReceivedOfType("session.update"));

        var status = await user.GetSessionAsync(sessionId);
        Assert.Equal("failed", status.String("state"));
        var retry = await user.ConnectExpectingRejectionAsync($"/ws/sessions/{sessionId}");
        Assert.Equal((409, "session_not_startable"), (retry.Status, retry.Code));
    }

    [Fact]
    public async Task SetupFailsWhenTheProviderReportsDifferentInstructions()
    {
        await using var app = await TestApp.StartAsync();
        app.Fake.Behavior.AppliedInstructions = sent => sent is null ? null : sent[..(sent.Length / 2)];
        using var user = await app.LoginAsync();
        var sessionId = await user.CreateSessionAsync();
        await using var socket = await user.ConnectAsync($"/ws/sessions/{sessionId}");

        var error = await socket.WaitForAsync("error");
        await socket.WaitForStateAsync("ended");
        await socket.WaitForClosedAsync();
        var provider = await app.Fake.WaitForConnectionAsync(0);
        await provider.WaitForClosedAsync();
        await app.WaitForLiveConnectionsReleasedAsync();

        Assert.Equal("provider_unavailable", error.String("code"));
        Assert.DoesNotContain(socket.Messages, m => m.Type is "session.ready" or "audio.delta");
        Assert.Empty(provider.ReceivedOfType("response.create"));
        Assert.Single(app.Fake.Connections);
        Assert.Equal("failed", (await user.GetSessionAsync(sessionId)).String("state"));
    }
}

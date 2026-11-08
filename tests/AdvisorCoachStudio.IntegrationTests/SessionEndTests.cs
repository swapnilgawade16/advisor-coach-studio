using AdvisorCoachStudio.IntegrationTests.Infrastructure;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Every way a live attempt ends (cap, HTTP fallback, browser loss) uses the same finalization path (spec 8.6, 18.2).</summary>
public sealed class SessionEndTests
{
    [Fact]
    public async Task DurationCapEndsTheSessionThroughTheNormalFinalizationPath()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        Assert.Equal(120, roleplay.Ready.Int("maxDurationSeconds"));
        await roleplay.HearAsync(Scripts.Opening);
        var mark = roleplay.Socket.LastIndex;

        app.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        var ending = await roleplay.Socket.WaitForStateAsync("ending", mark);
        var ended = await roleplay.Socket.WaitForAsync("session.ended", afterIndex: ending.Index);
        await roleplay.Socket.WaitForClosedAsync();
        await roleplay.Provider.WaitForClosedAsync();

        Assert.Equal("duration_limit", ended.String("reason"));
        Assert.Equal("complete", ended.String("transcriptStatus"));
        Assert.DoesNotContain(roleplay.Socket.Messages, m => m.Type == "error");
        Assert.Single(roleplay.Provider.ReceivedOfType("response.create"));
        var status = await user.GetSessionAsync(roleplay.SessionId);
        Assert.Equal("ended", status.String("state"));
        Assert.Equal("duration_limit", status.String("endReason"));
        var turns = (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(Lines.Opening, Assert.Single(turns).GetProperty("text").GetString());
        await app.WaitForLiveConnectionsReleasedAsync();
    }

    [Fact]
    public async Task HttpCompleteFallbackEndsALiveSessionIdempotently()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.HearAsync(Scripts.Opening);

        var accepted = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/complete");
        Assert.Equal(202, accepted.StatusCode);
        var ended = await roleplay.Socket.WaitForAsync("session.ended");
        await roleplay.Socket.WaitForClosedAsync();
        Assert.Equal("user_ended", ended.String("reason"));
        Assert.Equal("complete", ended.String("transcriptStatus"));

        await Wait.UntilAsync(async () => (await user.GetSessionAsync(roleplay.SessionId)).String("state") == "ended", "the session to reach the ended state");
        var again = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/complete");
        Assert.Equal(200, again.StatusCode);
        Assert.Equal("ended", again.String("state"));
        Assert.Single(roleplay.Socket.Messages, m => m.Type == "session.ended");
    }

    [Fact]
    public async Task BrowserDisconnectFinalizesTheTranscriptAndReleasesTheSlot()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.PlayConversationAsync(exchanges: 1);

        await roleplay.Socket.CloseAsync();
        await roleplay.Provider.WaitForClosedAsync();
        await app.WaitForLiveConnectionsReleasedAsync();

        await Wait.UntilAsync(async () => (await user.GetSessionAsync(roleplay.SessionId)).String("state") == "ended", "the session to reach the ended state");
        var status = await user.GetSessionAsync(roleplay.SessionId);
        Assert.Equal("disconnected", status.String("endReason"));
        var transcript = await user.GetTranscriptAsync(roleplay.SessionId);
        Assert.Equal("complete", transcript.String("status"));
        Assert.Equal(new[] { Lines.Opening, Lines.Advisor1, Lines.Client1 }, transcript.Json.GetProperty("turns").EnumerateArray().Select(t => t.GetProperty("text").GetString()));
    }
}

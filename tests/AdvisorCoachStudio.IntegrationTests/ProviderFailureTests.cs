using AdvisorCoachStudio.IntegrationTests.Infrastructure;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Provider failures end the attempt visibly while preserving completed turns (spec 8.6, 9.1, 16.2).</summary>
public sealed class ProviderFailureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderDisconnectEndsTheAttemptAndKeepsCompletedTurns(bool abrupt)
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        await roleplay.PlayConversationAsync(exchanges: 1);

        var mark = socket.LastIndex;
        if (abrupt)
        {
            roleplay.Provider.Abort();
        }
        else
        {
            await roleplay.Provider.CloseAsync();
        }

        var error = await socket.WaitForAsync("error", afterIndex: mark);
        var ended = await socket.WaitForAsync("session.ended", afterIndex: mark);
        await socket.WaitForClosedAsync();

        Assert.Equal("provider_disconnected", error.String("code"));
        Assert.True(error.Bool("retryable"));
        Assert.True(error.Index < ended.Index);
        Assert.Equal("provider_error", ended.String("reason"));
        Assert.Equal("incomplete", ended.String("transcriptStatus"));

        var transcript = await user.GetTranscriptAsync(roleplay.SessionId);
        Assert.Equal("incomplete", transcript.String("status"));
        Assert.Equal(1, transcript.Json.GetProperty("revision").GetInt32());
        var turns = transcript.Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(new[] { Lines.Opening, Lines.Advisor1, Lines.Client1 }, turns.Select(t => t.GetProperty("text").GetString()));
        Assert.All(turns, t => Assert.Equal("final", t.GetProperty("status").GetString()));

        var status = await user.GetSessionAsync(roleplay.SessionId);
        Assert.Equal("ended", status.String("state"));
        Assert.Equal("provider_error", status.String("endReason"));
        await app.WaitForLiveConnectionsReleasedAsync();
    }

    [Fact]
    public async Task FatalProviderErrorEventEndsTheAttemptWithoutLeakingProviderDetails()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        await roleplay.HearAsync(Scripts.Opening);

        // A benign error (nothing to cancel) is ignored; a server_error ends the attempt.
        await roleplay.Provider.SendErrorAsync("invalid_request_error", "response_cancel_not_active");
        await roleplay.Provider.SendErrorAsync("server_error", "fake_internal_failure");
        var error = await socket.WaitForAsync("error");
        var ended = await socket.WaitForAsync("session.ended");
        await socket.WaitForClosedAsync();

        Assert.Equal("provider_error", error.String("code"));
        Assert.DoesNotContain("fake_internal_failure", error.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic provider error", error.Raw, StringComparison.Ordinal);
        Assert.Single(socket.Messages, m => m.Type == "error");
        Assert.Equal("provider_error", ended.String("reason"));
        Assert.Equal("incomplete", ended.String("transcriptStatus"));
        var turns = (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(Lines.Opening, Assert.Single(turns).GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("{\"type\":\"response.output_audio.delta\",\"event_id\":\"evt_bad\",\"response_id\":\"resp_x\",\"item_id\":\"item_x\",\"output_index\":0,\"content_index\":0}")]
    [InlineData("{\"type\":\"response.output_audio_transcript.done\",\"response_id\":\"resp_x\",\"item_id\":\"item_x\",\"content_index\":0,\"transcript\":42}")]
    [InlineData("{not json")]
    public async Task MalformedProviderEventFailsVisiblyInsteadOfBeingIgnored(string providerEvent)
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        await roleplay.HearAsync(Scripts.Opening);

        // A required-shape mismatch is a contract failure, not something to silently skip (no empty audio or captions).
        await roleplay.Provider.SendRawAsync(providerEvent);
        var error = await socket.WaitForAsync("error");
        var ended = await socket.WaitForAsync("session.ended");

        Assert.Equal("provider_protocol_error", error.String("code"));
        Assert.Equal("provider_error", ended.String("reason"));
        var turns = (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(Lines.Opening, Assert.Single(turns).GetProperty("text").GetString());
    }
}

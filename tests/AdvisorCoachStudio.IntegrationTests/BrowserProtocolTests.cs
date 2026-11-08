using AdvisorCoachStudio.IntegrationTests.Infrastructure;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>The relay is a strict protocol boundary: invalid browser input ends the attempt but keeps completed turns (spec 8.3, 8.4).</summary>
public sealed class BrowserProtocolTests
{
    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"v\":1,\"type\":\"transcript.upload\",\"text\":\"I handled that perfectly\"}")]
    [InlineData("{\"v\":2,\"type\":\"heartbeat\"}")]
    [InlineData("{\"v\":1,\"type\":\"audio.append\",\"clientSequence\":1,\"audio\":\"AAEC\"}")]
    [InlineData("{\"v\":1,\"type\":\"playback.progress\",\"responseId\":\"resp_open\",\"itemId\":\"item_client_open\",\"contentIndex\":0,\"playedMilliseconds\":-5}")]
    public async Task MalformedBrowserMessageEndsTheAttemptWithAProtocolErrorAndKeepsCompletedTurns(string message)
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        await roleplay.HearAsync(Scripts.Opening);
        var mark = socket.LastIndex;

        await socket.SendTextAsync(message);

        var error = await socket.WaitForAsync("error", afterIndex: mark);
        var ended = await socket.WaitForAsync("session.ended", afterIndex: mark);
        await socket.WaitForClosedAsync();
        await roleplay.Provider.WaitForClosedAsync();
        await AssertProtocolFailureAsync(roleplay, user, error, ended);
        Assert.Empty(roleplay.Provider.ReceivedOfType("input_audio_buffer.append"));
    }

    [Fact]
    public async Task BinaryFrameIsAProtocolError()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.HearAsync(Scripts.Opening);
        var mark = roleplay.Socket.LastIndex;

        await roleplay.Socket.SendBinaryAsync(TestAudio.MicrophoneFrame(1));

        var error = await roleplay.Socket.WaitForAsync("error", afterIndex: mark);
        var ended = await roleplay.Socket.WaitForAsync("session.ended", afterIndex: mark);
        await AssertProtocolFailureAsync(roleplay, user, error, ended);
    }

    [Fact]
    public async Task ReplayedAudioSequenceIsAProtocolErrorAndIsNotForwarded()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.HearAsync(Scripts.Opening);
        var frame = Convert.ToBase64String(TestAudio.MicrophoneFrame(1));
        await roleplay.Socket.SendAsync(new { v = 1, type = "audio.append", clientSequence = 5, audio = frame });
        await roleplay.Provider.WaitForCountAsync("input_audio_buffer.append", 1);
        var mark = roleplay.Socket.LastIndex;

        await roleplay.Socket.SendAsync(new { v = 1, type = "audio.append", clientSequence = 5, audio = frame });

        var error = await roleplay.Socket.WaitForAsync("error", afterIndex: mark);
        var ended = await roleplay.Socket.WaitForAsync("session.ended", afterIndex: mark);
        await roleplay.Provider.WaitForClosedAsync();
        await AssertProtocolFailureAsync(roleplay, user, error, ended);
        Assert.Single(roleplay.Provider.ReceivedOfType("input_audio_buffer.append"));
    }

    [Fact]
    public async Task MicrophoneAudioFasterThanRealTimeEndsTheAttemptForConnectionQuality()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.HearAsync(Scripts.Opening);
        var mark = roleplay.Socket.LastIndex;

        // 500 ms chunks sent back to back: far beyond real time plus the bounded burst allowance.
        for (var i = 0; i < 16 && !roleplay.Socket.Closed.IsCompleted; i++)
        {
            try
            {
                await roleplay.Socket.SendAudioAsync(TestAudio.Pcm(500, 40 + i));
            }
            catch (Exception) when (roleplay.Socket.Closed.IsCompleted)
            {
                break;
            }
        }

        var error = await roleplay.Socket.WaitForAsync("error", afterIndex: mark);
        var ended = await roleplay.Socket.WaitForAsync("session.ended", afterIndex: mark);
        Assert.Equal("audio_rate_exceeded", error.String("code"));
        Assert.Equal("connection_quality", ended.String("reason"));
        await roleplay.Provider.WaitForClosedAsync();
        Assert.InRange(roleplay.Provider.ReceivedOfType("input_audio_buffer.append").Count, 1, 15);
        var turns = (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(Lines.Opening, Assert.Single(turns).GetProperty("text").GetString());
    }

    [Fact]
    public async Task MutedMicrophoneAudioIsNeverForwardedAndHeartbeatsKeepTheSessionAlive()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        await roleplay.HearAsync(Scripts.Opening);

        await socket.SendAsync(new { v = 1, type = "microphone.muted", muted = true });
        for (var i = 0; i < 5; i++)
        {
            await socket.SendAudioAsync(TestAudio.MicrophoneFrame(i));
        }

        await socket.SendHeartbeatAsync();
        await socket.SendAsync(new { v = 1, type = "microphone.muted", muted = false });
        var unmuted = await socket.SendAudioAsync(TestAudio.MicrophoneFrame(99));

        // Messages are handled in order, so once the unmuted frame arrives the muted ones were already dropped.
        var forwarded = await roleplay.Provider.WaitForAsync("input_audio_buffer.append");
        Assert.Equal(unmuted, forwarded.String("audio"));
        var ended = await roleplay.EndAsync();
        Assert.Equal("user_ended", ended.String("reason"));
        Assert.DoesNotContain(socket.Messages, m => m.Type == "error");
        Assert.Single(roleplay.Provider.ReceivedOfType("input_audio_buffer.append"));
    }

    private static async Task AssertProtocolFailureAsync(LiveRoleplay roleplay, TestUser user, JsonMessage error, JsonMessage ended)
    {
        Assert.Equal("protocol_error", error.String("code"));
        Assert.True(error.Index < ended.Index);
        Assert.Equal("disconnected", ended.String("reason"));
        Assert.Equal("complete", ended.String("transcriptStatus"));

        var transcript = await user.GetTranscriptAsync(roleplay.SessionId);
        Assert.Equal(1, transcript.Json.GetProperty("revision").GetInt32());
        var turn = Assert.Single(transcript.Json.GetProperty("turns").EnumerateArray());
        Assert.Equal(Lines.Opening, turn.GetProperty("text").GetString());
        Assert.Equal("final", turn.GetProperty("status").GetString());
        Assert.Equal("ended", (await user.GetSessionAsync(roleplay.SessionId)).String("state"));
    }
}

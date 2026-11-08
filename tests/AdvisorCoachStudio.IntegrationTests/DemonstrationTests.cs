using System.Text.Json;
using AdvisorCoachStudio.IntegrationTests.Infrastructure;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>AI-as-advisor demonstration: separate output-only relay that never alters the scored roleplay (spec 12).</summary>
public sealed class DemonstrationTests
{
    [Fact]
    public async Task DemonstrationIsOutputOnlyAndNeverChangesTheRoleplayTranscriptOrReport()
    {
        await using var app = await TestApp.StartAsync();
        app.Reports.Enqueue(call => ScriptedReportModelClient.Content(ReportContent.Valid(ReportContent.FromCall(call))));
        using var user = await app.LoginAsync();
        var sessionId = await PlayEndedRoleplayAsync(app, user, exchanges: 1);
        await user.WaitForReportStatusAsync(sessionId, "ready");
        var transcriptBefore = (await user.GetTranscriptAsync(sessionId)).Body;
        var reportBefore = (await user.GetReportAsync(sessionId)).Body;

        var created = await user.PostAsync($"/api/sessions/{sessionId}/demonstrations");
        Assert.Equal(201, created.StatusCode);
        var demonstrationId = created.Json.GetProperty("demonstrationId").GetGuid();
        Assert.Equal($"/ws/demonstrations/{demonstrationId}", created.String("webSocketPath"));
        Assert.Equal(90, created.Json.GetProperty("maxDurationSeconds").GetInt32());

        await using var socket = await user.ConnectAsync(created.String("webSocketPath"));
        var ready = await socket.WaitForAsync("session.ready");
        Assert.Equal(demonstrationId.ToString(), ready.String("sessionId"));
        var provider = await app.Fake.WaitForConnectionAsync(1);

        // Output-only provider session: no turn detection, no input transcription, rubric-owned focus text only.
        var update = await provider.WaitForAsync("session.update");
        var session = update.Property("session");
        var input = session.GetProperty("audio").GetProperty("input");
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
        Assert.False(input.TryGetProperty("transcription", out _));
        var instructions = session.GetProperty("instructions").GetString()!;
        Assert.Contains("Discovery and questioning", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Explore goals further", instructions, StringComparison.Ordinal);

        var demo = Scripts.Demonstration;
        await socket.WaitForCountAsync("audio.delta", demo.AudioChunks, m => m.String("responseId") == demo.ResponseId);
        var final = await socket.WaitForAsync("transcript.final", m => m.String("text") == demo.Transcript);
        Assert.Equal("demonstration", final.String("speaker"));
        Assert.All(socket.Messages.Where(m => m.Type is "transcript.partial" or "transcript.final"), m => Assert.Equal("demonstration", m.String("speaker")));

        // Microphone audio is a protocol violation on a demonstration socket and is never forwarded.
        var mark = socket.LastIndex;
        await socket.SendAudioAsync(TestAudio.MicrophoneFrame(1));
        var error = await socket.WaitForAsync("error", afterIndex: mark);
        var ended = await socket.WaitForAsync("session.ended", afterIndex: mark);
        await socket.WaitForClosedAsync();
        await provider.WaitForClosedAsync();

        Assert.Equal("protocol_error", error.String("code"));
        Assert.Equal("not_applicable", ended.String("reportStatus"));
        Assert.Equal(JsonValueKind.Null, ended.Property("reportUrl").ValueKind);
        Assert.Empty(provider.ReceivedOfType("input_audio_buffer.append"));
        Assert.Single(provider.ReceivedOfType("response.create"));

        // The scored roleplay transcript and report are exactly as before.
        Assert.Equal(transcriptBefore, (await user.GetTranscriptAsync(sessionId)).Body);
        Assert.Equal(reportBefore, (await user.GetReportAsync(sessionId)).Body);
        Assert.Single(app.Reports.Calls);
        Assert.Equal("ended", (await user.GetSessionAsync(sessionId)).String("state"));
    }

    [Fact]
    public async Task DemonstrationRequiresAnEndedSessionAndCompletesAfterPlayback()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        var unstarted = await user.CreateSessionAsync();
        var tooEarly = await user.PostAsync($"/api/sessions/{unstarted}/demonstrations");
        Assert.Equal((409, "session_not_ended"), (tooEarly.StatusCode, tooEarly.ProblemCode));

        // No advisor speech: the report is an honest limited-evidence outcome without any model call.
        var sessionId = await PlayEndedRoleplayAsync(app, user, exchanges: 0);
        var report = await user.WaitForReportStatusAsync(sessionId, "ready");
        Assert.Equal(JsonValueKind.Null, report.Json.GetProperty("report").GetProperty("overallScore").ValueKind);
        Assert.Equal(0, app.Reports.CallCount);

        var created = await user.PostAsync($"/api/sessions/{sessionId}/demonstrations");
        Assert.Equal(201, created.StatusCode);
        var path = created.String("webSocketPath");
        await using (var socket = await user.ConnectAsync(path))
        {
            var demo = Scripts.Demonstration;
            var audio = await socket.WaitForCountAsync("audio.delta", demo.AudioChunks, m => m.String("responseId") == demo.ResponseId);
            await socket.WaitForAsync("transcript.final", m => m.String("text") == demo.Transcript);
            Assert.DoesNotContain(socket.Messages, m => m.Type == "session.ended");

            await socket.SendPlaybackProgressAsync(demo.ResponseId, demo.ItemId, demo.TotalMilliseconds);
            var ended = await socket.WaitForAsync("session.ended", afterIndex: audio[^1].Index);
            await socket.WaitForClosedAsync();
            Assert.Equal("completed", ended.String("reason"));
            Assert.Equal("complete", ended.String("transcriptStatus"));
            Assert.DoesNotContain(socket.Messages, m => m.Type == "error");
        }

        await app.WaitForLiveConnectionsReleasedAsync();
        var replay = await user.ConnectExpectingRejectionAsync(path);
        Assert.Equal((409, "demonstration_not_startable"), (replay.Status, replay.Code));
        var turns = (await user.GetTranscriptAsync(sessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(Lines.Opening, Assert.Single(turns).GetProperty("text").GetString());
    }

    private static async Task<Guid> PlayEndedRoleplayAsync(TestApp app, TestUser user, int exchanges)
    {
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.PlayConversationAsync(exchanges);
        await roleplay.EndAsync();
        await app.WaitForLiveConnectionsReleasedAsync();
        return roleplay.SessionId;
    }
}

using System.Text.Json;
using AdvisorCoachStudio.IntegrationTests.Infrastructure;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>
/// Complete roleplay through the real relay, event parser, transcript assembler, and report pipeline, with only the
/// provider sockets and report model faked (spec 7, 8, 9, 10, 11.4, 18.2).
/// </summary>
public sealed class RoleplayHappyPathTests
{
    [Fact]
    public async Task FullRoleplayCapturesAnOrderedTranscriptAndStoresAValidatedReport()
    {
        await using var app = await TestApp.StartAsync();
        var reportGate = app.Reports.EnqueueGate();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        var provider = roleplay.Provider;

        // Lifecycle: "connecting" first, then session.ready with safe public metadata, then "ready".
        var connecting = await socket.WaitForStateAsync("connecting");
        Assert.Equal(0, connecting.Index);
        Assert.True(connecting.Index < roleplay.Ready.Index);
        await socket.WaitForStateAsync("ready", roleplay.Ready.Index);
        Assert.Equal(roleplay.SessionId.ToString(), roleplay.Ready.String("sessionId"));
        var wireFormat = roleplay.Ready.Property("audio");
        Assert.Equal("pcm16le", wireFormat.GetProperty("encoding").GetString());
        Assert.Equal(24_000, wireFormat.GetProperty("sampleRate").GetInt32());
        Assert.Equal(1, wireFormat.GetProperty("channels").GetInt32());
        Assert.Equal(480, wireFormat.GetProperty("frameSamples").GetInt32());
        Assert.Equal(120, roleplay.Ready.Int("maxDurationSeconds"));
        Assert.Equal("Alex Morgan", roleplay.Ready.String("clientDisplayName"));
        Assert.False(roleplay.Ready.Bool("simulated"));

        // Provider configuration: GA session.update (type realtime, server VAD) acknowledged before the single opening request.
        var update = await provider.WaitForAsync("session.update");
        var session = update.Property("session");
        var input = session.GetProperty("audio").GetProperty("input");
        Assert.Equal("realtime", session.GetProperty("type").GetString());
        Assert.Equal("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(24_000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal("whisper-1", input.GetProperty("transcription").GetProperty("model").GetString());
        Assert.Equal("server_vad", input.GetProperty("turn_detection").GetProperty("type").GetString());
        Assert.True(input.GetProperty("turn_detection").GetProperty("create_response").GetBoolean());
        Assert.True(input.GetProperty("turn_detection").GetProperty("interrupt_response").GetBoolean());
        Assert.Equal("alloy", session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
        Assert.Equal("test", provider.Model);
        var create = await provider.WaitForAsync("response.create");
        Assert.True(create.Index > update.Index);

        // Opening: provider audio reaches the browser unchanged with increasing seq; captions go partial -> final.
        var opening = await roleplay.HearAsync(Scripts.Opening, reportPlayback: false);
        var expectedAudio = Enumerable.Range(0, Scripts.Opening.AudioChunks).Select(Scripts.Opening.AudioChunkBase64).ToList();
        Assert.Equal(expectedAudio, opening.Audio.Select(a => a.String("audio")));
        Assert.All(opening.Audio, a => Assert.Equal(Scripts.Opening.ItemId, a.String("itemId")));
        var partial = await socket.WaitForAsync("transcript.partial", m => m.String("speaker") == "client");
        Assert.True(partial.Index < opening.Final.Index);
        Assert.Equal(opening.Final.String("turnId"), partial.String("turnId"));
        Assert.StartsWith(partial.String("text")!, Scripts.Opening.Transcript, StringComparison.Ordinal);
        Assert.Equal("final", opening.Final.String("status"));
        Assert.Equal(1, opening.Final.Int("sequence"));
        await socket.WaitForStateAsync("client_speaking");

        // "listening" is only sent once the browser reports the opening as actually played.
        Assert.DoesNotContain(socket.Messages, m => m.Type == "session.state" && m.String("state") == "listening");
        await socket.SendPlaybackProgressAsync(Scripts.Opening.ResponseId, Scripts.Opening.ItemId, Scripts.Opening.TotalMilliseconds);
        await socket.WaitForStateAsync("listening", opening.Audio[0].Index);
        Assert.Equal(1, provider.ResponseCreateCount);

        // Advisor turn 1: 20 ms frames reach the provider byte-for-byte; server VAD (not the relay) triggers the reply.
        var first = await roleplay.SpeakAsync(Lines.Advisor1);
        Assert.Equal(first.SentAudio, first.ForwardedAudio);
        Assert.All(first.SentAudio, audio => Assert.Equal(960, Convert.FromBase64String(audio).Length));
        Assert.Equal(2, first.Caption.Int("sequence"));
        Assert.Equal("transcript.final", first.Caption.Type);
        Assert.Equal("final", first.Caption.String("status"));
        Assert.Equal(JsonValueKind.Null, first.Caption.Property("fullyHeard").ValueKind);
        var reply1 = Scripts.Reply(1, Lines.Client1);
        await provider.StreamResponseAsync(reply1, transcriptDoneFragmentBytes: 16);
        var heard1 = await roleplay.HearAsync(reply1);
        Assert.Equal(3, heard1.Final.Int("sequence"));

        var second = await roleplay.SpeakAsync(Lines.Advisor2);
        Assert.Equal(second.SentAudio, second.ForwardedAudio);
        await roleplay.ReplyAsync(Scripts.Reply(2, Lines.Client2));
        await roleplay.SpeakAsync(Lines.Advisor3);
        await roleplay.ReplyAsync(Scripts.Reply(3, Lines.Client3));

        var serverSequence = socket.Messages.Where(m => m.Type == "audio.delta").Select(m => m.Long("seq")).ToList();
        Assert.Equal(Enumerable.Range(1, serverSequence.Count).Select(i => (long)i), serverSequence);
        Assert.Equal(1, provider.ResponseCreateCount);

        // End: one idempotent finalization path, complete transcript, report queued.
        var ended = await roleplay.EndAsync();
        Assert.Equal("user_ended", ended.String("reason"));
        Assert.Equal("complete", ended.String("transcriptStatus"));
        Assert.Contains(ended.String("reportStatus"), new[] { "queued", "generating" });
        Assert.Equal($"/sessions/{roleplay.SessionId}/report", ended.String("reportUrl"));
        await socket.WaitForStateAsync("ended", ended.Index);
        await provider.WaitForClosedAsync();
        Assert.Single(provider.ReceivedOfType("response.create"));
        Assert.Equal(30, provider.ReceivedOfType("input_audio_buffer.append").Count);
        Assert.DoesNotContain(provider.Received, e => e.Type is "input_audio_buffer.commit" or "response.cancel" or "conversation.item.truncate");

        // Transcript revision 1 follows provider item order with canonical sequences.
        var transcript = await user.GetTranscriptAsync(roleplay.SessionId);
        Assert.Equal(200, transcript.StatusCode);
        Assert.True(transcript.IsNoStore);
        Assert.Equal(1, transcript.Json.GetProperty("revision").GetInt32());
        Assert.Equal("complete", transcript.String("status"));
        Assert.Contains("speech recognition", transcript.String("notice"), StringComparison.Ordinal);
        var turns = transcript.Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Range(1, 7), turns.Select(t => t.GetProperty("sequence").GetInt32()));
        Assert.Equal(Enumerable.Range(1, 7).Select(i => $"T{i}"), turns.Select(t => t.GetProperty("label").GetString()));
        Assert.Equal(new[] { "client", "advisor", "client", "advisor", "client", "advisor", "client" }, turns.Select(t => t.GetProperty("speaker").GetString()));
        Assert.Equal(
            new[] { Lines.Opening, Lines.Advisor1, Lines.Client1, Lines.Advisor2, Lines.Client2, Lines.Advisor3, Lines.Client3 },
            turns.Select(t => t.GetProperty("text").GetString()));
        Assert.All(turns, t => Assert.Equal("final", t.GetProperty("status").GetString()));
        Assert.All(turns, t => Assert.Equal(
            t.GetProperty("speaker").GetString() == "client" ? JsonValueKind.True : JsonValueKind.Null,
            t.GetProperty("fullyHeard").ValueKind));

        // Report: the model sees T-labels of the frozen revision; quotes must be exact substrings; the server scores.
        var call = await Wait.ForAsync(reportGate.Started, "the report model call");
        Assert.Equal(turns.Select(t => t.GetProperty("label").GetString()), call.Transcript.Select(t => t.TurnId));
        Assert.All(call.Transcript, t => Assert.True(t.Quotable));
        Assert.Equal("generating", (await user.GetReportAsync(roleplay.SessionId)).String("status"));
        reportGate.Release(ScriptedReportModelClient.Content(ReportContent.Valid(ReportContent.FromTranscript(transcript.Json), [4, 3, 3, 3, null, null])));

        var ready = await user.WaitForReportStatusAsync(roleplay.SessionId, "ready");
        Assert.True(ready.IsNoStore);
        var report = ready.Json.GetProperty("report");
        Assert.Equal(3.3m, report.GetProperty("overallScore").GetDecimal()); // (4+3+3+3)/4 = 3.25, rounded away from zero
        Assert.Equal(4, report.GetProperty("scoredCategoryCount").GetInt32());
        Assert.Equal(3, report.GetProperty("usableAdvisorTurnCount").GetInt32());
        Assert.Equal("sufficientevidence", report.GetProperty("assessmentStatus").GetString()!.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);
        Assert.Equal(1, report.GetProperty("transcriptRevision").GetInt32());
        Assert.Equal("fake-report-deployment", report.GetProperty("modelDeployment").GetString());
        Assert.Equal(roleplay.SessionId, report.GetProperty("sessionId").GetGuid());

        var labels = report.GetProperty("turnLabels");
        var textByLabel = turns.ToDictionary(t => t.GetProperty("label").GetString()!, t => t.GetProperty("text").GetString()!);
        foreach (var turn in turns)
        {
            Assert.Equal(turn.GetProperty("turnId").GetGuid(), labels.GetProperty(turn.GetProperty("label").GetString()!).GetGuid());
        }

        var evidence = report.GetProperty("content").GetProperty("skillScores").EnumerateArray()
            .SelectMany(score => score.GetProperty("evidence").EnumerateArray())
            .ToList();
        Assert.Equal(4, evidence.Count);
        Assert.All(evidence, e => Assert.Contains(e.GetProperty("quote").GetString()!, textByLabel[e.GetProperty("turnId").GetString()!], StringComparison.Ordinal));

        var call1 = Assert.Single(app.Reports.Calls);
        Assert.False(call1.IsCorrection);
        var retry = await user.PostAsync($"/api/sessions/{roleplay.SessionId}/report/retry");
        Assert.Equal((409, "report_ready"), (retry.StatusCode, retry.ProblemCode));
    }
}

using AdvisorCoachStudio.IntegrationTests.Infrastructure;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Transcript integrity: provider item order, late transcription, and the bounded finalization drain (spec 8.6, 10).</summary>
public sealed class TranscriptOrderingTests
{
    [Fact]
    public async Task AdvisorTranscriptionCompletingAfterTheNextResponseStartedKeepsProviderItemOrder()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.HearAsync(Scripts.Opening);

        // The advisor turn is committed, but its transcription only completes after the client's reply has played.
        var advisor = await roleplay.SpeakAsync(Lines.Advisor1, completeTranscription: false);
        var reply = Scripts.Reply(1, Lines.Client1);
        var heard = await roleplay.ReplyAsync(reply);
        Assert.Equal(3, heard.Final.Int("sequence"));
        Assert.DoesNotContain(roleplay.Socket.Messages, m => m.Type == "transcript.final" && m.String("speaker") == "advisor");

        var advisorFinal = await roleplay.CompleteTranscriptionAsync(advisor.ItemId, Lines.Advisor1);
        Assert.Equal(2, advisorFinal.Int("sequence"));
        Assert.True(advisorFinal.Index > heard.Final.Index);

        var ended = await roleplay.EndAsync();
        Assert.Equal("complete", ended.String("transcriptStatus"));
        var turns = (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(new[] { "client", "advisor", "client" }, turns.Select(t => t.GetProperty("speaker").GetString()));
        Assert.Equal(new[] { Lines.Opening, Lines.Advisor1, Lines.Client1 }, turns.Select(t => t.GetProperty("text").GetString()));
        Assert.Equal(new[] { 1, 2, 3 }, turns.Select(t => t.GetProperty("sequence").GetInt32()));
        Assert.Equal(advisorFinal.String("turnId"), turns[1].GetProperty("turnId").GetString());
    }

    [Fact]
    public async Task AdvisorTranscriptionArrivingDuringTheFinalizationDrainIsKept()
    {
        await using var app = await TestApp.StartAsync(new TestAppOptions { Settings = new Dictionary<string, string> { ["Realtime:FinalizationDrainSeconds"] = "10" } });
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        await roleplay.HearAsync(Scripts.Opening);
        var advisor = await roleplay.SpeakAsync(Lines.Advisor1, completeTranscription: false);

        var mark = socket.LastIndex;
        await socket.SendEndAsync();
        await socket.WaitForStateAsync("ending", mark);
        Assert.DoesNotContain(socket.Messages, m => m.Type == "session.ended");

        await roleplay.Provider.SendInputTranscriptionCompletedAsync(advisor.ItemId, Lines.Advisor1);
        var ended = await socket.WaitForAsync("session.ended", afterIndex: mark);

        Assert.Equal("user_ended", ended.String("reason"));
        Assert.Equal("complete", ended.String("transcriptStatus"));
        Assert.Contains(socket.Messages, m => m.Type == "transcript.final" && m.String("text") == Lines.Advisor1 && m.Index < ended.Index);
        var turns = (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(new[] { Lines.Opening, Lines.Advisor1 }, turns.Select(t => t.GetProperty("text").GetString()));
        Assert.Empty(roleplay.Provider.ReceivedOfType("input_audio_buffer.commit"));
        Assert.Empty(roleplay.Provider.ReceivedOfType("response.cancel"));
    }

    [Fact]
    public async Task MissingAdvisorTranscriptionLeavesAnIncompleteTranscriptAfterTheBoundedDrain()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.HearAsync(Scripts.Opening);
        await roleplay.SpeakAsync(Lines.Advisor1, completeTranscription: false);

        var ended = await roleplay.EndAsync();

        Assert.Equal("user_ended", ended.String("reason"));
        Assert.Equal("incomplete", ended.String("transcriptStatus"));
        var transcript = await user.GetTranscriptAsync(roleplay.SessionId);
        Assert.Equal("incomplete", transcript.String("status"));
        var turns = transcript.Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(new[] { "final", "pending" }, turns.Select(t => t.GetProperty("status").GetString()));
        Assert.Equal(Lines.Opening, turns[0].GetProperty("text").GetString());
        Assert.Equal(LiveRoleplay.FirstWord(Lines.Advisor1), turns[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task EndingDuringUncommittedAdvisorSpeechCommitsItOnceAndKeepsTheTurn()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        var provider = roleplay.Provider;
        await roleplay.HearAsync(Scripts.Opening);

        for (var i = 0; i < 10; i++)
        {
            await socket.SendAudioAsync(TestAudio.MicrophoneFrame(i));
        }

        await provider.WaitForCountAsync("input_audio_buffer.append", 10);
        await provider.SendSpeechStartedAsync("item_user_1");

        // Sync point: the relay forwards this partial only after it processed speech_started (provider events are FIFO).
        await provider.SendInputTranscriptionDeltaAsync("item_user_1", "I can");
        await socket.WaitForAsync("transcript.partial", m => m.String("speaker") == "advisor");
        var mark = socket.LastIndex;
        await socket.SendEndAsync();

        // The relay commits the in-progress speech so it can be transcribed, and never asks for another reply.
        await provider.WaitForAsync("input_audio_buffer.commit");
        var previous = provider.LastItemId;
        await provider.SendCommittedAsync("item_user_1", previous);
        await provider.SendUserItemAddedAsync("item_user_1", previous);
        await provider.SendInputTranscriptionCompletedAsync("item_user_1", Lines.Advisor1);
        var ended = await socket.WaitForAsync("session.ended", afterIndex: mark);
        await socket.WaitForClosedAsync();
        await provider.WaitForClosedAsync();

        Assert.Equal("complete", ended.String("transcriptStatus"));
        Assert.Single(provider.ReceivedOfType("input_audio_buffer.commit"));
        Assert.Single(provider.ReceivedOfType("response.create"));
        var turns = (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(new[] { Lines.Opening, Lines.Advisor1 }, turns.Select(t => t.GetProperty("text").GetString()));
    }
}

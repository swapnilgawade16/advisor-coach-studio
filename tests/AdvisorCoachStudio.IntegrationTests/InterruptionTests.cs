using AdvisorCoachStudio.IntegrationTests.Infrastructure;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Barge-in: clear playback, cancel, truncate to the played position, and never play cancelled audio (spec 8.5).</summary>
public sealed class InterruptionTests
{
    [Fact]
    public async Task AdvisorSpeechDuringClientPlaybackClearsCancelsAndTruncatesToThePlayedPosition()
    {
        await using var app = await TestApp.StartAsync();
        var holdOpening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 2.5 s of opening audio: the relay releases at most 2 s of unplayed audio, then paces by playback progress.
        var opening = Scripts.Opening with { AudioChunks = 25, HoldBeforeDone = holdOpening.Task };
        app.Fake.Behavior.ResponseScript = (connection, number) => number == 0 ? opening : null;
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        var socket = roleplay.Socket;
        var provider = roleplay.Provider;

        try
        {
            // The opening is still generating (no response.done). Reporting 300 ms played releases 300 ms more audio,
            // which also proves the relay processed the progress before the advisor starts speaking.
            await socket.WaitForCountAsync("audio.delta", 20, m => m.String("responseId") == opening.ResponseId);
            await socket.SendPlaybackProgressAsync(opening.ResponseId, opening.ItemId, 300);
            var audio = await socket.WaitForCountAsync("audio.delta", 23, m => m.String("responseId") == opening.ResponseId);
            var mark = socket.LastIndex;
            await provider.SendSpeechStartedAsync("item_user_1", audioStartMs: 0);

            var clear = await socket.WaitForAsync("playback.clear", m => m.String("responseId") == opening.ResponseId, afterIndex: mark);
            Assert.Equal(opening.ItemId, clear.String("itemId"));
            await socket.WaitForStateAsync("interrupted", mark);
            await socket.SendPlaybackStoppedAsync(opening.ResponseId, opening.ItemId, 300);

            var cancel = await provider.WaitForAsync("response.cancel");
            Assert.Equal(opening.ResponseId, cancel.String("response_id"));
            var truncate = await provider.WaitForAsync("conversation.item.truncate");
            Assert.Equal(opening.ItemId, truncate.String("item_id"));
            Assert.Equal(0, truncate.Int("content_index"));
            Assert.Equal(300, truncate.Int("audio_end_ms"));
            await socket.WaitForStateAsync("listening", clear.Index);

            // Buffered and in-flight audio for the cancelled response must never reach the browser.
            for (var i = 0; i < 3; i++)
            {
                await provider.SendAudioDeltaAsync(opening.ResponseId, opening.ItemId, TestAudio.Pcm(100, 900 + i));
            }

            await provider.SendResponseDoneAsync(opening.ResponseId, "cancelled", "turn_detected", opening.ItemId, opening.Transcript);
            var previous = provider.LastItemId;
            await provider.SendSpeechStoppedAsync("item_user_1", 400);
            await provider.SendCommittedAsync("item_user_1", previous);
            await provider.SendUserItemAddedAsync("item_user_1", previous);
            var advisorFinal = await roleplay.CompleteTranscriptionAsync("item_user_1", Lines.Advisor1);

            Assert.DoesNotContain(socket.Messages, m => m.Type == "audio.delta" && m.Index > clear.Index);
            Assert.Equal(23, socket.Messages.Count(m => m.Type == "audio.delta"));
            var clientFinal = await socket.WaitForAsync("transcript.final", m => m.String("speaker") == "client");
            Assert.True(clientFinal.Index < advisorFinal.Index);
            Assert.Equal("interrupted", clientFinal.String("status"));
            Assert.False(clientFinal.Bool("fullyHeard"));
            Assert.True(audio[^1].Index < clear.Index);

            var ended = await roleplay.EndAsync();
            Assert.Equal("complete", ended.String("transcriptStatus"));
            await provider.WaitForClosedAsync();
            Assert.Single(provider.ReceivedOfType("response.cancel"));
            Assert.Single(provider.ReceivedOfType("conversation.item.truncate"));
            Assert.Single(provider.ReceivedOfType("response.create"));

            var turns = (await user.GetTranscriptAsync(roleplay.SessionId)).Json.GetProperty("turns").EnumerateArray().ToList();
            Assert.Equal(new[] { "client", "advisor" }, turns.Select(t => t.GetProperty("speaker").GetString()));
            Assert.Equal("interrupted", turns[0].GetProperty("status").GetString());
            Assert.False(turns[0].GetProperty("fullyHeard").GetBoolean());
            Assert.Equal("final", turns[1].GetProperty("status").GetString());
            Assert.Equal(Lines.Advisor1, turns[1].GetProperty("text").GetString());
        }
        finally
        {
            holdOpening.TrySetCanceled(TestContext.Current.CancellationToken);
        }
    }
}

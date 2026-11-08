namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>Azure GA realtime server events (synthetic shapes matching the documented GA names).</summary>
public sealed partial class FakeRealtimeConnection
{
    public string NextEventId() => $"event_fake_{Index}_{Interlocked.Increment(ref _eventIds)}";

    public Task SendErrorAsync(string type, string? code) =>
        SendAsync(new { type = "error", event_id = NextEventId(), error = new { type, code, message = "Synthetic provider error.", param = (string?)null } });

    // ---- Advisor (user) input --------------------------------------------------------------

    public Task SendSpeechStartedAsync(string itemId, int audioStartMs = 0) =>
        SendAsync(new { type = "input_audio_buffer.speech_started", event_id = NextEventId(), audio_start_ms = audioStartMs, item_id = itemId });

    public Task SendSpeechStoppedAsync(string itemId, int audioEndMs) =>
        SendAsync(new { type = "input_audio_buffer.speech_stopped", event_id = NextEventId(), audio_end_ms = audioEndMs, item_id = itemId });

    public Task SendCommittedAsync(string itemId, string? previousItemId) =>
        SendAsync(new { type = "input_audio_buffer.committed", event_id = NextEventId(), previous_item_id = previousItemId, item_id = itemId });

    public async Task SendUserItemAddedAsync(string itemId, string? previousItemId)
    {
        await SendAsync(new
        {
            type = "conversation.item.added",
            event_id = NextEventId(),
            previous_item_id = previousItemId,
            item = new { id = itemId, @object = "realtime.item", type = "message", status = "completed", role = "user", content = new[] { new { type = "input_audio", transcript = (string?)null } } },
        });
        SetLastItem(itemId);
    }

    public Task SendInputTranscriptionDeltaAsync(string itemId, string delta) =>
        SendAsync(new { type = "conversation.item.input_audio_transcription.delta", event_id = NextEventId(), item_id = itemId, content_index = 0, delta });

    public Task SendInputTranscriptionCompletedAsync(string itemId, string transcript) =>
        SendAsync(new { type = "conversation.item.input_audio_transcription.completed", event_id = NextEventId(), item_id = itemId, content_index = 0, transcript });

    /// <summary>Server VAD sequence for one advisor turn: speech_started, speech_stopped, committed, conversation.item.added(user).</summary>
    public async Task CommitAdvisorTurnAsync(string itemId, int audioMs, bool speechStartedAlreadySent = false)
    {
        var previous = LastItemId;
        if (!speechStartedAlreadySent)
        {
            await SendSpeechStartedAsync(itemId);
        }

        await SendSpeechStoppedAsync(itemId, audioMs);
        await SendCommittedAsync(itemId, previous);
        await SendUserItemAddedAsync(itemId, previous);
    }

    // ---- Client (assistant) output ---------------------------------------------------------

    public Task SendResponseCreatedAsync(string responseId) =>
        SendAsync(new { type = "response.created", event_id = NextEventId(), response = new { id = responseId, @object = "realtime.response", status = "in_progress", output = Array.Empty<object>() } });

    public Task SendOutputItemAddedAsync(string responseId, string itemId) =>
        SendAsync(new
        {
            type = "response.output_item.added",
            event_id = NextEventId(),
            response_id = responseId,
            output_index = 0,
            item = new { id = itemId, @object = "realtime.item", type = "message", status = "in_progress", role = "assistant", content = Array.Empty<object>() },
        });

    public async Task SendAssistantItemAddedAsync(string itemId, string? previousItemId)
    {
        await SendAsync(new
        {
            type = "conversation.item.added",
            event_id = NextEventId(),
            previous_item_id = previousItemId,
            item = new { id = itemId, @object = "realtime.item", type = "message", status = "in_progress", role = "assistant", content = Array.Empty<object>() },
        });
        SetLastItem(itemId);
    }

    public Task SendAudioDeltaAsync(string responseId, string itemId, byte[] pcm) =>
        SendAsync(new { type = "response.output_audio.delta", event_id = NextEventId(), response_id = responseId, item_id = itemId, output_index = 0, content_index = 0, delta = Convert.ToBase64String(pcm) });

    public Task SendTranscriptDeltaAsync(string responseId, string itemId, string delta) =>
        SendAsync(new { type = "response.output_audio_transcript.delta", event_id = NextEventId(), response_id = responseId, item_id = itemId, output_index = 0, content_index = 0, delta });

    public Task SendAudioDoneAsync(string responseId, string itemId) =>
        SendAsync(new { type = "response.output_audio.done", event_id = NextEventId(), response_id = responseId, item_id = itemId, output_index = 0, content_index = 0 });

    public Task SendTranscriptDoneAsync(string responseId, string itemId, string transcript, int fragmentBytes = int.MaxValue) =>
        SendFragmentedAsync(new { type = "response.output_audio_transcript.done", event_id = NextEventId(), response_id = responseId, item_id = itemId, output_index = 0, content_index = 0, transcript }, fragmentBytes);

    public Task SendOutputItemDoneAsync(string responseId, string itemId, string? transcript) =>
        SendAsync(new { type = "response.output_item.done", event_id = NextEventId(), response_id = responseId, output_index = 0, item = AssistantItem(itemId, "completed", transcript) });

    /// <summary>response.done; <paramref name="reason"/> fills status_details (e.g. "turn_detected" for a cancelled response).</summary>
    public Task SendResponseDoneAsync(string responseId, string status, string? reason, string itemId, string? transcript)
    {
        object? details = reason is null ? null : new { type = status, reason };
        return SendAsync(new
        {
            type = "response.done",
            event_id = NextEventId(),
            response = new
            {
                id = responseId,
                @object = "realtime.response",
                status,
                status_details = details,
                output = new[] { AssistantItem(itemId, status == "completed" ? "completed" : "incomplete", transcript) },
                usage = new { total_tokens = 30, input_tokens = 20, output_tokens = 10 },
            },
        });
    }

    /// <summary>Streams a complete scripted response (honouring <see cref="ScriptedResponse.HoldBeforeDone"/>).</summary>
    public async Task StreamResponseAsync(ScriptedResponse script, int transcriptDoneFragmentBytes = int.MaxValue)
    {
        var previous = LastItemId;
        await SendResponseCreatedAsync(script.ResponseId);
        await SendOutputItemAddedAsync(script.ResponseId, script.ItemId);
        await SendAssistantItemAddedAsync(script.ItemId, previous);
        var deltas = script.TranscriptDeltas();
        for (var i = 0; i < script.AudioChunks; i++)
        {
            await SendAudioDeltaAsync(script.ResponseId, script.ItemId, script.AudioChunk(i));
            if (i < deltas.Count)
            {
                await SendTranscriptDeltaAsync(script.ResponseId, script.ItemId, deltas[i]);
            }
        }

        if (script.HoldBeforeDone is { } hold)
        {
            await hold.WaitAsync(_lifetime.Token);
        }

        await SendAudioDoneAsync(script.ResponseId, script.ItemId);
        await SendTranscriptDoneAsync(script.ResponseId, script.ItemId, script.Transcript, transcriptDoneFragmentBytes);
        await SendOutputItemDoneAsync(script.ResponseId, script.ItemId, script.Transcript);
        await SendResponseDoneAsync(script.ResponseId, "completed", null, script.ItemId, script.Transcript);
    }

    private static object AssistantItem(string itemId, string status, string? transcript) => new
    {
        id = itemId,
        @object = "realtime.item",
        type = "message",
        status,
        role = "assistant",
        content = new[] { new { type = "output_audio", transcript } },
    };
}

using System.Text.Json;

namespace AdvisorCoachStudio.LiveCheck;

internal sealed record ReceivedEvent(JsonElement Raw, TimeSpan At);

internal sealed class ResponseInfo
{
    public required string Id { get; init; }
    public TimeSpan FirstSeenAt { get; init; }
    public TimeSpan? FirstAudioAt { get; set; }
    public TimeSpan? DoneAt { get; set; }
    public long AudioBytes { get; set; }
    public bool OddAudioChunk { get; set; }
    public string? ItemId { get; set; }
    public int ContentIndex { get; set; }
    public string Transcript { get; set; } = "";
    public string? Status { get; set; }
    public string? StatusReason { get; set; }
    public bool Done { get; set; }

    /// <summary>Generated audio duration (24 kHz PCM16 = 48 bytes per millisecond).</summary>
    public int AudioMilliseconds => (int)(AudioBytes / 48);
}

/// <summary>Conversation state rebuilt from raw provider JSON, independently of the application's parser.</summary>
internal sealed class ConversationState
{
    public JsonElement? SessionCreated { get; private set; }
    public JsonElement? SessionUpdated { get; private set; }
    public List<string> PendingErrors { get; } = [];
    public List<string> BenignErrors { get; } = [];
    public List<(string ItemId, TimeSpan At)> SpeechStarted { get; } = [];
    public List<(string ItemId, TimeSpan At)> SpeechStopped { get; } = [];
    public List<string> AdvisorItems { get; } = [];
    public Dictionary<string, string> AdvisorTranscripts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AdvisorFailures { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ResponseInfo> Responses { get; } = new(StringComparer.Ordinal);
    public List<string> ResponseOrder { get; } = [];
    public List<(string ItemId, int AudioEndMs)> Truncations { get; } = [];
    public SortedDictionary<string, int> EventCounts { get; } = new(StringComparer.Ordinal);
    public int TotalEvents { get; private set; }

    public int AdvisorOutcomes => AdvisorItems.Count;

    public void Apply(ReceivedEvent received)
    {
        var raw = received.Raw;
        var type = Json.Str(raw, "type") ?? "(missing type)";
        TotalEvents++;
        EventCounts[type] = EventCounts.TryGetValue(type, out var count) ? count + 1 : 1;
        switch (type)
        {
            case "error":
            {
                var error = Json.Obj(raw, "error");
                var code = Json.Str(error, "code");
                if (code is "response_cancel_not_active" or "input_audio_buffer_commit_empty")
                {
                    BenignErrors.Add(code);
                }
                else
                {
                    PendingErrors.Add($"{Json.Str(error, "type") ?? "error"}/{code ?? "-"}: {Json.Str(error, "message") ?? "(no message)"}");
                }

                break;
            }

            case "session.created":
                SessionCreated = raw;
                break;
            case "session.updated":
                SessionUpdated = raw;
                break;
            case "input_audio_buffer.speech_started":
                SpeechStarted.Add((Json.Str(raw, "item_id") ?? "", received.At));
                break;
            case "input_audio_buffer.speech_stopped":
                SpeechStopped.Add((Json.Str(raw, "item_id") ?? "", received.At));
                break;
            case "conversation.item.input_audio_transcription.completed":
            {
                var itemId = Json.Str(raw, "item_id") ?? "";
                if (AdvisorTranscripts.TryAdd(itemId, (Json.Str(raw, "transcript") ?? "").Trim()))
                {
                    AdvisorItems.Add(itemId);
                }

                break;
            }

            case "conversation.item.input_audio_transcription.failed":
            {
                var itemId = Json.Str(raw, "item_id") ?? "";
                if (AdvisorFailures.TryAdd(itemId, Json.Str(Json.Obj(raw, "error"), "message") ?? "(no message)"))
                {
                    AdvisorItems.Add(itemId);
                }

                break;
            }

            case "response.created":
                GetResponse(Json.Str(Json.Obj(raw, "response"), "id"), received.At);
                break;
            case "response.output_audio.delta":
            {
                var response = GetResponse(Json.Str(raw, "response_id"), received.At);
                var length = DecodedLength(Json.Str(raw, "delta"));
                response.AudioBytes += length;
                response.OddAudioChunk |= length % 2 != 0;
                response.FirstAudioAt ??= received.At;
                response.ItemId ??= Json.Str(raw, "item_id");
                response.ContentIndex = Json.Int(raw, "content_index") ?? 0;
                break;
            }

            case "response.output_audio_transcript.done":
                GetResponse(Json.Str(raw, "response_id"), received.At).Transcript = (Json.Str(raw, "transcript") ?? "").Trim();
                break;
            case "response.done":
            {
                var body = Json.Obj(raw, "response");
                var response = GetResponse(Json.Str(body, "id"), received.At);
                response.Done = true;
                response.DoneAt = received.At;
                response.Status = Json.Str(body, "status");
                var details = Json.Obj(body, "status_details");
                response.StatusReason = Json.Str(details, "reason") ?? Json.Str(details, "type");
                if (response.Transcript.Length == 0)
                {
                    response.Transcript = TranscriptFromOutput(body);
                }

                break;
            }

            case "conversation.item.truncated":
                Truncations.Add((Json.Str(raw, "item_id") ?? "", Json.Int(raw, "audio_end_ms") ?? -1));
                break;
        }
    }

    private ResponseInfo GetResponse(string? id, TimeSpan at)
    {
        id ??= "(missing id)";
        if (!Responses.TryGetValue(id, out var response))
        {
            response = new ResponseInfo { Id = id, FirstSeenAt = at };
            Responses[id] = response;
            ResponseOrder.Add(id);
        }

        return response;
    }

    private static int DecodedLength(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return 0;
        }

        try
        {
            return Convert.FromBase64String(base64).Length;
        }
        catch (FormatException)
        {
            return 1; // Reported as an odd (invalid) audio chunk.
        }
    }

    private static string TranscriptFromOutput(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(content.EnumerateArray().Select(part => Json.Str(part, "transcript")).OfType<string>().Select(text => text.Trim()));
            }
        }

        return string.Join(' ', parts);
    }
}

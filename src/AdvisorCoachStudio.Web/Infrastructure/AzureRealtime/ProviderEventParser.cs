using System.Collections.Frozen;
using System.Text.Json;

namespace AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

public sealed class ProviderProtocolException(string eventType, string field)
    : Exception($"Provider event '{eventType}' is missing or has an invalid '{field}'.")
{
    public string EventType { get; } = eventType;
    public string Field { get; } = field;
}

public sealed record ProviderOutputPart(string ItemId, int ContentIndex, string? Role, string? Transcript);

/// <summary>Normalized Azure GA realtime server event. Only metadata and the fields the relay needs are retained.</summary>
public sealed record ProviderEvent
{
    public required string Type { get; init; }
    public bool IsKnown { get; init; }
    public string? EventId { get; init; }
    public string? ItemId { get; init; }
    public string? PreviousItemId { get; init; }
    public string? ResponseId { get; init; }
    public int ContentIndex { get; init; }
    public string? Delta { get; init; }
    public string? Transcript { get; init; }
    public int? AudioStartMs { get; init; }
    public int? AudioEndMs { get; init; }
    public string? ItemRole { get; init; }
    public string? ItemType { get; init; }
    public string? ResponseStatus { get; init; }
    public string? ResponseStatusReason { get; init; }
    public IReadOnlyList<ProviderOutputPart> ResponseOutput { get; init; } = [];
    public string? ErrorType { get; init; }
    public string? ErrorCode { get; init; }
    public string? ProviderSessionId { get; init; }

    /// <summary>The effective instructions echoed in session.created/session.updated, when the provider includes them.</summary>
    public string? SessionInstructions { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

/// <summary>
/// Parses GA event JSON. Required-shape mismatches for supported events throw <see cref="ProviderProtocolException"/>
/// so failures are visible instead of silently producing empty audio or transcripts.
/// </summary>
public static class ProviderEventParser
{
    private static readonly FrozenSet<string> IgnoredKnownTypes = new[]
    {
        "conversation.created", "conversation.item.retrieved", "conversation.item.deleted", "input_audio_buffer.cleared",
        "input_audio_buffer.timeout_triggered", "response.content_part.added", "response.content_part.done",
        "response.output_text.delta", "response.output_text.done", "rate_limits.updated",
        "conversation.item.input_audio_transcription.segment",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static ProviderEvent Parse(ReadOnlyMemory<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new ProviderProtocolException("(unknown)", "type");
        }

        var type = typeElement.GetString()!;
        var eventId = OptionalString(root, "event_id");

        switch (type)
        {
            case "session.created":
            case "session.updated":
            {
                var session = RequiredObject(root, type, "session");
                return new ProviderEvent
                {
                    Type = type, IsKnown = true, EventId = eventId,
                    ProviderSessionId = OptionalString(session, "id"),
                    SessionInstructions = OptionalString(session, "instructions"),
                };
            }

            case "error":
            {
                var error = RequiredObject(root, type, "error");
                return new ProviderEvent
                {
                    Type = type, IsKnown = true, EventId = eventId,
                    ErrorType = OptionalString(error, "type") ?? "unknown",
                    ErrorCode = OptionalString(error, "code"),
                };
            }

            case "input_audio_buffer.speech_started":
                return new ProviderEvent { Type = type, IsKnown = true, EventId = eventId, ItemId = RequiredString(root, type, "item_id"), AudioStartMs = RequiredInt(root, type, "audio_start_ms") };

            case "input_audio_buffer.speech_stopped":
                return new ProviderEvent { Type = type, IsKnown = true, EventId = eventId, ItemId = RequiredString(root, type, "item_id"), AudioEndMs = RequiredInt(root, type, "audio_end_ms") };

            case "input_audio_buffer.committed":
                return new ProviderEvent { Type = type, IsKnown = true, EventId = eventId, ItemId = RequiredString(root, type, "item_id"), PreviousItemId = OptionalString(root, "previous_item_id") };

            case "conversation.item.added":
            case "conversation.item.done":
            {
                var item = RequiredObject(root, type, "item");
                return new ProviderEvent
                {
                    Type = type, IsKnown = true, EventId = eventId,
                    ItemId = RequiredString(item, type, "item.id"),
                    PreviousItemId = OptionalString(root, "previous_item_id"),
                    ItemRole = OptionalString(item, "role"),
                    ItemType = OptionalString(item, "type"),
                };
            }

            case "conversation.item.input_audio_transcription.delta":
                return new ProviderEvent { Type = type, IsKnown = true, EventId = eventId, ItemId = RequiredString(root, type, "item_id"), ContentIndex = RequiredInt(root, type, "content_index"), Delta = RequiredText(root, type, "delta") };

            case "conversation.item.input_audio_transcription.completed":
                return new ProviderEvent { Type = type, IsKnown = true, EventId = eventId, ItemId = RequiredString(root, type, "item_id"), ContentIndex = RequiredInt(root, type, "content_index"), Transcript = RequiredText(root, type, "transcript") };

            case "conversation.item.input_audio_transcription.failed":
            {
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
                return new ProviderEvent
                {
                    Type = type, IsKnown = true, EventId = eventId,
                    ItemId = RequiredString(root, type, "item_id"), ContentIndex = RequiredInt(root, type, "content_index"),
                    ErrorType = error.ValueKind == JsonValueKind.Object ? OptionalString(error, "type") : null,
                    ErrorCode = error.ValueKind == JsonValueKind.Object ? OptionalString(error, "code") : null,
                };
            }

            case "conversation.item.truncated":
                return new ProviderEvent { Type = type, IsKnown = true, EventId = eventId, ItemId = RequiredString(root, type, "item_id"), ContentIndex = RequiredInt(root, type, "content_index"), AudioEndMs = RequiredInt(root, type, "audio_end_ms") };

            case "response.created":
            {
                var response = RequiredObject(root, type, "response");
                return new ProviderEvent { Type = type, IsKnown = true, EventId = eventId, ResponseId = RequiredString(response, type, "response.id"), ResponseStatus = OptionalString(response, "status") };
            }

            case "response.output_item.added":
            case "response.output_item.done":
            {
                var item = RequiredObject(root, type, "item");
                return new ProviderEvent
                {
                    Type = type, IsKnown = true, EventId = eventId,
                    ResponseId = RequiredString(root, type, "response_id"),
                    ItemId = RequiredString(item, type, "item.id"),
                    ItemRole = OptionalString(item, "role"),
                    ItemType = OptionalString(item, "type"),
                };
            }

            case "response.output_audio.delta":
            case "response.output_audio_transcript.delta":
                return new ProviderEvent
                {
                    Type = type, IsKnown = true, EventId = eventId,
                    ResponseId = RequiredString(root, type, "response_id"), ItemId = RequiredString(root, type, "item_id"),
                    ContentIndex = RequiredInt(root, type, "content_index"), Delta = RequiredText(root, type, "delta"),
                };

            case "response.output_audio.done":
                return new ProviderEvent
                {
                    Type = type, IsKnown = true, EventId = eventId,
                    ResponseId = RequiredString(root, type, "response_id"), ItemId = RequiredString(root, type, "item_id"),
                    ContentIndex = RequiredInt(root, type, "content_index"),
                };

            case "response.output_audio_transcript.done":
                return new ProviderEvent
                {
                    Type = type, IsKnown = true, EventId = eventId,
                    ResponseId = RequiredString(root, type, "response_id"), ItemId = RequiredString(root, type, "item_id"),
                    ContentIndex = RequiredInt(root, type, "content_index"), Transcript = RequiredText(root, type, "transcript"),
                };

            case "response.done":
                return ParseResponseDone(root, type, eventId);

            default:
                return new ProviderEvent { Type = type, IsKnown = IgnoredKnownTypes.Contains(type), EventId = eventId };
        }
    }

    private static ProviderEvent ParseResponseDone(JsonElement root, string type, string? eventId)
    {
        var response = RequiredObject(root, type, "response");
        var status = RequiredString(response, type, "response.status");
        string? reason = null;
        if (response.TryGetProperty("status_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            reason = OptionalString(details, "reason") ?? OptionalString(details, "type");
        }

        var parts = new List<ProviderOutputPart>();
        if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || OptionalString(item, "id") is not { } itemId)
                {
                    continue;
                }

                var role = OptionalString(item, "role");
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var part in content.EnumerateArray())
                    {
                        var transcript = part.ValueKind == JsonValueKind.Object ? OptionalString(part, "transcript") : null;
                        parts.Add(new ProviderOutputPart(itemId, index, role, transcript));
                        index++;
                    }
                }
            }
        }

        int? inputTokens = null, outputTokens = null;
        if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inputTokens = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv) ? iv : null;
            outputTokens = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov) ? ov : null;
        }

        return new ProviderEvent
        {
            Type = type, IsKnown = true, EventId = eventId,
            ResponseId = RequiredString(response, type, "response.id"),
            ResponseStatus = status, ResponseStatusReason = reason, ResponseOutput = parts,
            InputTokens = inputTokens, OutputTokens = outputTokens,
        };
    }

    private static JsonElement RequiredObject(JsonElement root, string type, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Object
            ? element
            : throw new ProviderProtocolException(type, name);

    private static string RequiredString(JsonElement root, string type, string field)
    {
        var name = field.Contains('.', StringComparison.Ordinal) ? field[(field.LastIndexOf('.') + 1)..] : field;
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value
            ? value
            : throw new ProviderProtocolException(type, field);
    }

    /// <summary>Text fields may legitimately be empty strings (e.g. empty transcripts) but must be present.</summary>
    private static string RequiredText(JsonElement root, string type, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new ProviderProtocolException(type, name);

    private static int RequiredInt(JsonElement root, string type, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value >= 0
            ? value
            : throw new ProviderProtocolException(type, name);

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}

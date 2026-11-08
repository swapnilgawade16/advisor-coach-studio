using System.Buffers;
using System.Text.Json;

namespace AdvisorCoachStudio.Web.Contracts;

/// <summary>Application WebSocket protocol constants (see docs/application-protocol.md).</summary>
public static class AppProtocol
{
    public const string Subprotocol = "acs.v1";
    public const int Version = 1;
    public const int MaxMessageBytes = 32 * 1024;
    public const int MaxControlBytes = 8 * 1024;
    public const int MaxAudioBytesPerMessage = 24_000;
    public const int MaxIdLength = 128;
    public const int WireSampleRate = 24_000;
    public const int BytesPerMillisecond = WireSampleRate * 2 / 1000; // 48
    public const int FrameSamples = 480;
    public const int MaxOutputChunkBytes = 9_600; // 200 ms

    public static int BytesToMilliseconds(long bytes) => (int)(bytes / BytesPerMillisecond);
}

public sealed class AppProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public abstract record BrowserMessage;

public sealed record AudioAppendMessage(long ClientSequence, string AudioBase64, int ByteLength) : BrowserMessage
{
    public int DurationMs => AppProtocol.BytesToMilliseconds(ByteLength);
}

public sealed record PlaybackPositionMessage(string ResponseId, string ItemId, int ContentIndex, int PlayedMilliseconds, bool Stopped) : BrowserMessage;

public sealed record MicrophoneMutedMessage(bool Muted) : BrowserMessage;

public sealed record SessionEndMessage(string Reason) : BrowserMessage;

public sealed record HeartbeatMessage : BrowserMessage;

/// <summary>Strict parser for browser messages. Rejects unknown types, wrong versions, and malformed fields.</summary>
public static class BrowserMessageParser
{
    public static BrowserMessage Parse(ReadOnlySpan<byte> utf8, bool allowAudio)
    {
        if (utf8.Length > AppProtocol.MaxMessageBytes)
        {
            throw new AppProtocolException("message_too_large", "Message exceeds the maximum size.");
        }

        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 4 });
            document = JsonDocument.ParseValue(ref reader);
            try
            {
                // ParseValue stops after the first value; anything else in the frame (a second message or garbage) is invalid.
                if (reader.Read())
                {
                    document.Dispose();
                    throw new AppProtocolException("malformed_message", "Message contains trailing data.");
                }
            }
            catch (JsonException)
            {
                document.Dispose();
                throw new AppProtocolException("malformed_message", "Message contains trailing data.");
            }
        }
        catch (JsonException)
        {
            throw new AppProtocolException("malformed_message", "Message is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AppProtocolException("malformed_message", "Message must be a JSON object.");
            }

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != AppProtocol.Version)
            {
                throw new AppProtocolException("unsupported_version", "Unsupported protocol version.");
            }

            var type = RequiredString(root, "type", 64);
            if (type != "audio.append" && utf8.Length > AppProtocol.MaxControlBytes)
            {
                throw new AppProtocolException("message_too_large", "Control message exceeds the maximum size.");
            }

            return type switch
            {
                "audio.append" when allowAudio => ParseAudio(root),
                "audio.append" => throw new AppProtocolException("audio_not_allowed", "This connection does not accept microphone audio."),
                "playback.progress" => ParsePosition(root, stopped: false),
                "playback.stopped" => ParsePosition(root, stopped: true),
                "microphone.muted" => new MicrophoneMutedMessage(RequiredBool(root, "muted")),
                "session.end" => new SessionEndMessage(OptionalString(root, "reason", 32) ?? "user"),
                "heartbeat" => new HeartbeatMessage(),
                _ => throw new AppProtocolException("unknown_message_type", "Unknown message type."),
            };
        }
    }

    private static AudioAppendMessage ParseAudio(JsonElement root)
    {
        if (!root.TryGetProperty("clientSequence", out var seqElement) || seqElement.ValueKind != JsonValueKind.Number || !seqElement.TryGetInt64(out var sequence) || sequence < 1)
        {
            throw new AppProtocolException("invalid_sequence", "clientSequence must be a positive integer.");
        }

        var audio = RequiredString(root, "audio", AppProtocol.MaxMessageBytes);
        var maxBytes = (audio.Length / 4 * 3) + 3;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4, maxBytes));
        try
        {
            if (!Convert.TryFromBase64String(audio, buffer, out var written))
            {
                throw new AppProtocolException("invalid_audio", "Audio must be valid base64.");
            }

            if (written == 0 || written % 2 != 0)
            {
                throw new AppProtocolException("invalid_audio", "Audio must contain whole 16-bit samples.");
            }

            if (written > AppProtocol.MaxAudioBytesPerMessage)
            {
                throw new AppProtocolException("invalid_audio", "Audio chunk is too large.");
            }

            return new AudioAppendMessage(sequence, audio, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static PlaybackPositionMessage ParsePosition(JsonElement root, bool stopped)
    {
        var responseId = RequiredString(root, "responseId", AppProtocol.MaxIdLength);
        var itemId = RequiredString(root, "itemId", AppProtocol.MaxIdLength);
        var contentIndex = RequiredInt(root, "contentIndex", 0, 64);
        var played = RequiredInt(root, "playedMilliseconds", 0, 3_600_000);
        return new PlaybackPositionMessage(responseId, itemId, contentIndex, played, stopped);
    }

    private static string RequiredString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new AppProtocolException("malformed_message", $"'{name}' is required.");
        }

        var value = element.GetString()!;
        if (value.Length == 0 || value.Length > maxLength)
        {
            throw new AppProtocolException("malformed_message", $"'{name}' has an invalid length.");
        }

        return value;
    }

    private static string? OptionalString(JsonElement root, string name, int maxLength) =>
        root.TryGetProperty(name, out var element) && element.ValueKind != JsonValueKind.Null ? RequiredString(root, name, maxLength) : null;

    private static bool RequiredBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : throw new AppProtocolException("malformed_message", $"'{name}' must be a boolean.");

    private static int RequiredInt(JsonElement root, string name, int min, int max)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < min || value > max)
        {
            throw new AppProtocolException("malformed_message", $"'{name}' must be an integer between {min} and {max}.");
        }

        return value;
    }
}

using System.Buffers;
using System.Text.Json;

namespace AdvisorCoachStudio.Web.Contracts;

/// <summary>Builds server-to-browser application messages with exact, documented field names.</summary>
public static class AppMessages
{
    public static byte[] State(string state) => Write("session.state", w => w.WriteString("state", state));

    public static byte[] Ready(Guid sessionId, int maxDurationSeconds, DateTimeOffset endsAtUtc, string clientDisplayName, string scenarioTitle, bool simulated) =>
        Write("session.ready", w =>
        {
            w.WriteString("sessionId", sessionId);
            w.WriteStartObject("audio");
            w.WriteString("encoding", "pcm16le");
            w.WriteNumber("sampleRate", AppProtocol.WireSampleRate);
            w.WriteNumber("channels", 1);
            w.WriteNumber("frameSamples", AppProtocol.FrameSamples);
            w.WriteEndObject();
            w.WriteNumber("maxDurationSeconds", maxDurationSeconds);
            w.WriteString("endsAtUtc", endsAtUtc);
            w.WriteString("clientDisplayName", clientDisplayName);
            w.WriteString("scenarioTitle", scenarioTitle);
            w.WriteBoolean("simulated", simulated);
        });

    public static byte[] AudioDelta(long seq, string responseId, string itemId, int contentIndex, ReadOnlySpan<byte> pcm)
    {
        var base64 = Convert.ToBase64String(pcm);
        return Write("audio.delta", w =>
        {
            w.WriteNumber("seq", seq);
            w.WriteString("responseId", responseId);
            w.WriteString("itemId", itemId);
            w.WriteNumber("contentIndex", contentIndex);
            w.WriteString("audio", base64);
        });
    }

    public static byte[] PlaybackClear(string responseId, string itemId) => Write("playback.clear", w =>
    {
        w.WriteString("responseId", responseId);
        w.WriteString("itemId", itemId);
    });

    public static byte[] TranscriptPartial(Guid turnId, string speaker, string text) => Write("transcript.partial", w =>
    {
        w.WriteString("turnId", turnId);
        w.WriteString("speaker", speaker);
        w.WriteString("text", text);
    });

    public static byte[] TranscriptFinal(Guid turnId, int sequence, string speaker, string text, string status, bool? fullyHeard) =>
        Write("transcript.final", w =>
        {
            w.WriteString("turnId", turnId);
            w.WriteNumber("sequence", sequence);
            w.WriteString("speaker", speaker);
            w.WriteString("text", text);
            w.WriteString("status", status);
            if (fullyHeard is { } heard)
            {
                w.WriteBoolean("fullyHeard", heard);
            }
            else
            {
                w.WriteNull("fullyHeard");
            }
        });

    public static byte[] Ended(string reason, string transcriptStatus, string reportStatus, string? reportUrl) => Write("session.ended", w =>
    {
        w.WriteString("reason", reason);
        w.WriteString("transcriptStatus", transcriptStatus);
        w.WriteString("reportStatus", reportStatus);
        if (reportUrl is null)
        {
            w.WriteNull("reportUrl");
        }
        else
        {
            w.WriteString("reportUrl", reportUrl);
        }
    });

    public static byte[] Error(string code, string message, string correlationId, bool retryable) => Write("error", w =>
    {
        w.WriteString("code", code);
        w.WriteString("message", message);
        w.WriteString("correlationId", correlationId);
        w.WriteBoolean("retryable", retryable);
    });

    private static byte[] Write(string type, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", AppProtocol.Version);
            writer.WriteString("type", type);
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}

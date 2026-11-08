using System.Buffers;
using System.Text.Json;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;

namespace AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

/// <summary>Builds Azure GA realtime client events (verified shapes: docs/azure-realtime-protocol.md).</summary>
public static class ProviderClientEvents
{
    public static byte[] SessionUpdateRoleplay(string instructions, AzureRealtimeSessionSettings settings, RealtimeOptions realtime) =>
        Write("session.update", w =>
        {
            w.WriteStartObject("session");
            w.WriteString("type", "realtime");
            w.WriteString("instructions", instructions);
            WriteAudioOnly(w);
            w.WriteStartObject("audio");
            w.WriteStartObject("input");
            WritePcmFormat(w);
            w.WriteStartObject("transcription");
            w.WriteString("model", settings.TranscriptionModel);
            w.WriteEndObject();
            w.WriteStartObject("turn_detection");
            w.WriteString("type", "server_vad");
            w.WriteNumber("threshold", realtime.VadThreshold);
            w.WriteNumber("prefix_padding_ms", realtime.VadPrefixPaddingMs);
            w.WriteNumber("silence_duration_ms", realtime.VadSilenceDurationMs);
            w.WriteBoolean("create_response", true);
            w.WriteBoolean("interrupt_response", true);
            w.WriteEndObject();
            w.WriteEndObject();
            WriteOutput(w, settings.Voice);
            w.WriteEndObject();
            w.WriteEndObject();
        });

    /// <summary>Output-only demonstration: no turn detection and no input transcription.</summary>
    public static byte[] SessionUpdateDemonstration(string instructions, AzureRealtimeSessionSettings settings) =>
        Write("session.update", w =>
        {
            w.WriteStartObject("session");
            w.WriteString("type", "realtime");
            w.WriteString("instructions", instructions);
            WriteAudioOnly(w);
            w.WriteStartObject("audio");
            w.WriteStartObject("input");
            WritePcmFormat(w);
            w.WriteNull("turn_detection");
            w.WriteEndObject();
            WriteOutput(w, settings.Voice);
            w.WriteEndObject();
            w.WriteEndObject();
        });

    public static byte[] AppendAudio(string base64Pcm) => Write("input_audio_buffer.append", w => w.WriteString("audio", base64Pcm));

    public static byte[] CommitAudio() => Write("input_audio_buffer.commit", _ => { });

    public static byte[] ResponseCreate() => Write("response.create", _ => { });

    public static byte[] ResponseCancel(string? responseId) => Write("response.cancel", w =>
    {
        if (responseId is not null)
        {
            w.WriteString("response_id", responseId);
        }
    });

    public static byte[] Truncate(string itemId, int contentIndex, int audioEndMs) => Write("conversation.item.truncate", w =>
    {
        w.WriteString("item_id", itemId);
        w.WriteNumber("content_index", contentIndex);
        w.WriteNumber("audio_end_ms", audioEndMs);
    });

    private static void WriteAudioOnly(Utf8JsonWriter w)
    {
        w.WriteStartArray("output_modalities");
        w.WriteStringValue("audio");
        w.WriteEndArray();
    }

    private static void WritePcmFormat(Utf8JsonWriter w)
    {
        w.WriteStartObject("format");
        w.WriteString("type", "audio/pcm");
        w.WriteNumber("rate", 24000);
        w.WriteEndObject();
    }

    private static void WriteOutput(Utf8JsonWriter w, string? voice)
    {
        w.WriteStartObject("output");
        WritePcmFormat(w);
        if (!string.IsNullOrWhiteSpace(voice))
        {
            w.WriteString("voice", voice);
        }

        w.WriteEndObject();
    }

    private static byte[] Write(string type, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event_id", "evt_" + Guid.NewGuid().ToString("N"));
            writer.WriteString("type", type);
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>Validated, non-secret session settings passed to the event builders.</summary>
public sealed record AzureRealtimeSessionSettings(string TranscriptionModel, string? Voice);

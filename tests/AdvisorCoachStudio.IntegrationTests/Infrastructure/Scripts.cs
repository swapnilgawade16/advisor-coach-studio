using System.Buffers.Binary;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>Deterministic synthetic PCM16 mono 24 kHz audio (never real recordings).</summary>
public static class TestAudio
{
    public const int SampleRate = 24_000;
    public const int BytesPerMillisecond = SampleRate * 2 / 1000;

    public static byte[] Pcm(int milliseconds, int seed)
    {
        var bytes = new byte[milliseconds * BytesPerMillisecond];
        var frequency = 160 + (Math.Abs(seed) % 97 * 5);
        for (var n = 0; n < bytes.Length / 2; n++)
        {
            var sample = (short)((Math.Sin(2 * Math.PI * frequency * n / SampleRate) * 3000) + (seed % 11));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(n * 2), sample);
        }

        return bytes;
    }

    /// <summary>One ~20 ms browser capture frame: 480 samples / 960 bytes.</summary>
    public static byte[] MicrophoneFrame(int seed) => Pcm(20, 5_000 + seed);
}

/// <summary>
/// A scripted client (assistant) response streamed by the fake provider with GA event names: response.created,
/// response.output_item.added, conversation.item.added, audio and transcript deltas, then the done events.
/// </summary>
public sealed record ScriptedResponse(string ResponseId, string ItemId, string Transcript)
{
    public int AudioChunks { get; init; } = 4;

    public int ChunkMilliseconds { get; init; } = 100;

    /// <summary>Awaited after all deltas and before the done events. A gate that never completes keeps the response in progress.</summary>
    public Task? HoldBeforeDone { get; init; }

    public int TotalMilliseconds => AudioChunks * ChunkMilliseconds;

    public byte[] AudioChunk(int index) => TestAudio.Pcm(ChunkMilliseconds, StableSeed(ResponseId) + index);

    public string AudioChunkBase64(int index) => Convert.ToBase64String(AudioChunk(index));

    /// <summary>Splits the transcript into one delta per audio chunk; the deltas concatenate to the transcript.</summary>
    public IReadOnlyList<string> TranscriptDeltas()
    {
        var words = Transcript.Split(' ');
        var parts = Math.Max(1, Math.Min(AudioChunks, words.Length));
        var deltas = new List<string>();
        for (var i = 0; i < parts; i++)
        {
            var start = i * words.Length / parts;
            var end = (i + 1) * words.Length / parts;
            var text = string.Join(' ', words[start..end]);
            deltas.Add(i == 0 ? text : " " + text);
        }

        return deltas;
    }

    private static int StableSeed(string text)
    {
        var hash = 17;
        foreach (var c in text)
        {
            hash = unchecked((hash * 31) + c);
        }

        return hash & 0x7FFF;
    }
}

/// <summary>Synthetic conversation lines (fictional; no real client data).</summary>
public static class Lines
{
    public const string Opening = "I checked my investments this morning and I'm really worried. I think I should sell everything before it gets worse.";
    public const string Advisor1 = "I can hear how worrying this morning has been for you, and I'm glad you called.";
    public const string Client1 = "It has been awful. Should I just sell everything before it drops any further?";
    public const string Advisor2 = "Before we decide anything, can you tell me what this money is meant to do for you?";
    public const string Client2 = "It is for my retirement, and that is not very far away now.";
    public const string Advisor3 = "Let's look at your retirement timeline together and agree a sensible next step today.";
    public const string Client3 = "Okay, that sounds sensible. When can we sit down and go through it?";
    public const string Demonstration = "Here I acknowledge the worry first. I can hear this has been unsettling. Before we decide anything, what matters most to you right now?";
}

public static class Scripts
{
    public static ScriptedResponse Opening { get; } = new("resp_open", "item_client_open", Lines.Opening);

    public static ScriptedResponse Demonstration { get; } = new("resp_demo", "item_demo", Lines.Demonstration) { AudioChunks = 5 };

    public static ScriptedResponse Reply(int number, string transcript) => new($"resp_reply_{number}", $"item_client_reply_{number}", transcript);
}

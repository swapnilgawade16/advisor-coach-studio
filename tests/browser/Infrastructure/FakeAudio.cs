using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// WAV files (48 kHz mono PCM16) for Chromium's --use-file-for-fake-audio-capture switch, written next to the test
/// assembly. The file loops while capture runs.
/// </summary>
/// <remarks>
/// What the simulator's energy gate sees (RMS per 20 ms frame, "loud" above 0.02; speech starts after 3 loud frames and
/// ends after 30 quiet frames): the default fake device's 20 ms beep leaves Chromium's audio processing as 3-4 loud
/// frames every 500 ms, so speech starts at the first beep and never ends until the session ends. The voice-like
/// bursts below give 0.9 s of speech followed by 1.7 s of silence, so every burst becomes one complete advisor turn.
/// Silence never triggers the gate, so the client's opening line plays to the end and no advisor turn exists.
/// </remarks>
public static class FakeAudio
{
    private const int SampleRate = 48_000;
    private const double LeadSilenceSeconds = 0.3;
    private const double BurstSeconds = 0.9;
    private const double PeriodSeconds = 2.6;
    private static readonly ConcurrentDictionary<string, Lazy<string>> Files = new(StringComparer.Ordinal);

    public static string SpeechBurstsFile() => Ensure("advisor-speech-bursts.wav", () => Samples(PeriodSeconds, Speech));

    public static string SilenceFile() => Ensure("silence.wav", () => Samples(1.0, _ => 0));

    // Written once per test process, so a file left by an older build is never reused.
    private static string Ensure(string name, Func<double[]> create) =>
        Files.GetOrAdd(name, _ => new Lazy<string>(() =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fake-audio", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Wav(create()));
            return path;
        })).Value;

    private static double[] Samples(double seconds, Func<double, double> signal)
    {
        var samples = new double[(int)(seconds * SampleRate)];
        for (var n = 0; n < samples.Length; n++)
        {
            samples[n] = signal(n / (double)SampleRate);
        }

        return samples;
    }

    private static double Speech(double time)
    {
        var t = time - LeadSilenceSeconds;
        if (t is < 0 or >= BurstSeconds)
        {
            return 0;
        }

        // Harmonic "vowel" with a gliding pitch, a 4 Hz syllable envelope, and 40 ms raised-cosine edges.
        var edge = Math.Min(1, Math.Min(t, BurstSeconds - t) / 0.04);
        var envelope = (0.5 - (0.5 * Math.Cos(Math.PI * edge))) * (0.65 + (0.35 * Math.Pow(Math.Sin(Math.PI * 4 * t), 2)));
        var phase = 2 * Math.PI * ((160 * t) - (30 / (2 * Math.PI * 1.3) * Math.Cos(2 * Math.PI * 1.3 * t)));
        double sum = 0;
        for (var k = 1; k <= 10; k++)
        {
            sum += Math.Sin(k * phase) / k;
        }

        return 0.3 * envelope * sum;
    }

    private static byte[] Wav(double[] samples)
    {
        var dataLength = samples.Length * 2;
        var wav = new byte[44 + dataLength];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        for (var n = 0; n < samples.Length; n++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (n * 2))..], (short)Math.Round(Math.Clamp(samples[n], -1, 1) * 32767));
        }

        return wav;
    }
}

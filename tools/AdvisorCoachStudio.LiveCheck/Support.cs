using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AdvisorCoachStudio.Web.Prompts;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.LiveCheck;

internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal static class RepoPaths
{
    public static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AdvisorCoachStudio.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Run the live check from inside the AdvisorCoachStudio repository.");
    }
}

/// <summary>Tolerant readers for raw provider JSON (independent of the application's strict parser).</summary>
internal static class Json
{
    public static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    public static double? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    public static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) ? value.GetBoolean() : null;

    public static string Strings(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? string.Join(',', value.EnumerateArray().Select(item => item.ToString()))
            : "-";
}

internal static class TextTools
{
    public static string Hash(string value) => PromptFingerprint.Of(value);

    /// <summary>Share of the expected line's distinct words that also appear in the actual text.</summary>
    public static double WordOverlap(string expected, string actual)
    {
        var expectedWords = Words(expected);
        var actualWords = Words(actual);
        return expectedWords.Count == 0 ? 0 : (double)expectedWords.Count(actualWords.Contains) / expectedWords.Count;
    }

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Replace('\u2019', '\'')
            .Split([' ', ',', '.', '!', '?', ';', ':', '"', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
}

/// <summary>Reads PCM WAV files in the wire format (24 kHz, 16-bit, mono) and returns the raw sample bytes.</summary>
internal static class WavFile
{
    public static async Task<byte[]> ReadPcm16Mono24kAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidOperationException($"'{path}' is not a WAV file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        byte[]? data = null;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var id = bytes.AsSpan(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var body = offset + 8;
            if (size < 0 || body + size > bytes.Length)
            {
                size = bytes.Length - body;
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = bytes.AsSpan(body, size & ~1).ToArray();
            }

            offset = body + size + (size & 1);
        }

        if (data is null || format != 1 || channels != 1 || rate != 24000 || bits != 16)
        {
            throw new InvalidOperationException(
                $"'{path}' must be PCM WAV at 24000 Hz, 16-bit, mono (found format {format}, {channels} channel(s), {rate} Hz, {bits}-bit).");
        }

        return data;
    }
}

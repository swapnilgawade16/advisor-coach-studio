using System.Text.Json;
using System.Text.RegularExpressions;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Contracts;

namespace AdvisorCoachStudio.UnitTests.Contracts;

/// <summary>
/// Server-to-browser messages must use exactly the field names documented in docs/application-protocol.md.
/// The expected fields are read from the document's "Server → browser" table, not duplicated here.
/// </summary>
public sealed partial class AppMessagesTests
{
    private static readonly Guid SessionId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid TurnId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    private sealed record DocumentedField(string Name, IReadOnlyList<string> Detail);

    private static Dictionary<string, byte[]> Samples() => new(StringComparer.Ordinal)
    {
        ["session.state"] = AppMessages.State("listening"),
        ["session.ready"] = AppMessages.Ready(SessionId, 120, new DateTimeOffset(2026, 1, 15, 9, 2, 0, TimeSpan.Zero), "Alex Morgan", "Synthetic title", simulated: false),
        ["audio.delta"] = AppMessages.AudioDelta(7, "resp_1", "item_1", 0, new byte[] { 1, 0, 2, 0 }),
        ["playback.clear"] = AppMessages.PlaybackClear("resp_1", "item_1"),
        ["transcript.partial"] = AppMessages.TranscriptPartial(TurnId, "client", "Partial synthetic text"),
        ["transcript.final"] = AppMessages.TranscriptFinal(TurnId, 3, "client", "Final synthetic text", "interrupted", fullyHeard: false),
        ["session.ended"] = AppMessages.Ended("user_ended", "complete", "queued", "/sessions/synthetic/report"),
        ["error"] = AppMessages.Error("protocol_error", "Synthetic safe message.", "corr-synthetic", retryable: true),
    };

    [Fact]
    public void EveryDocumentedServerMessageHasABuilder()
    {
        var documented = DocumentedServerMessages();

        Assert.Equal(8, documented.Count);
        Assert.Equal(documented.Keys.Order(StringComparer.Ordinal), Samples().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FieldNamesMatchTheProtocolDocumentExactly()
    {
        var documented = DocumentedServerMessages();

        foreach (var (type, payload) in Samples())
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("v").GetInt32());
            Assert.Equal(type, root.GetProperty("type").GetString());

            var expected = documented[type].Select(field => field.Name).Order(StringComparer.Ordinal).ToList();
            var actual = root.EnumerateObject().Select(p => p.Name).Where(name => name is not ("v" or "type")).Order(StringComparer.Ordinal).ToList();
            Assert.True(expected.SequenceEqual(actual), $"{type}: expected [{string.Join(", ", expected)}] but found [{string.Join(", ", actual)}].");

            foreach (var field in documented[type].Where(field => field.Detail.Count > 0))
            {
                var value = root.GetProperty(field.Name);
                if (value.ValueKind == JsonValueKind.Object)
                {
                    Assert.Equal(field.Detail.Order(StringComparer.Ordinal), value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
                }
                else
                {
                    Assert.Contains(value.GetString(), field.Detail);
                }
            }
        }
    }

    [Fact]
    public void ReadyMessageDescribesTheWireAudioFormat()
    {
        using var document = JsonDocument.Parse(Samples()["session.ready"]);
        var root = document.RootElement;
        var audio = root.GetProperty("audio");

        Assert.Equal(SessionId, root.GetProperty("sessionId").GetGuid());
        Assert.Equal("pcm16le", audio.GetProperty("encoding").GetString());
        Assert.Equal(24_000, audio.GetProperty("sampleRate").GetInt32());
        Assert.Equal(1, audio.GetProperty("channels").GetInt32());
        Assert.Equal(480, audio.GetProperty("frameSamples").GetInt32());
        Assert.Equal(120, root.GetProperty("maxDurationSeconds").GetInt32());
        Assert.False(root.GetProperty("simulated").GetBoolean());
    }

    [Fact]
    public void AudioDeltaCarriesBase64PcmAndSequence()
    {
        byte[] pcm = [1, 0, 2, 0, 255, 127];

        using var document = JsonDocument.Parse(AppMessages.AudioDelta(42, "resp_9", "item_9", 1, pcm));
        var root = document.RootElement;

        Assert.Equal(42, root.GetProperty("seq").GetInt64());
        Assert.Equal(1, root.GetProperty("contentIndex").GetInt32());
        Assert.Equal(pcm, root.GetProperty("audio").GetBytesFromBase64());
    }

    [Fact]
    public void NullableFieldsAreAlwaysPresent()
    {
        using var final = JsonDocument.Parse(AppMessages.TranscriptFinal(TurnId, 2, "advisor", "Synthetic", "final", fullyHeard: null));
        using var heard = JsonDocument.Parse(AppMessages.TranscriptFinal(TurnId, 2, "client", "Synthetic", "final", fullyHeard: true));
        using var ended = JsonDocument.Parse(AppMessages.Ended("provider_error", "incomplete", "failed", reportUrl: null));

        Assert.Equal(JsonValueKind.Null, final.RootElement.GetProperty("fullyHeard").ValueKind);
        Assert.True(heard.RootElement.GetProperty("fullyHeard").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ended.RootElement.GetProperty("reportUrl").ValueKind);
    }

    /// <summary>Parses the "Server → browser" table: type → fields, with parenthesized sub-fields or allowed values.</summary>
    private static Dictionary<string, IReadOnlyList<DocumentedField>> DocumentedServerMessages()
    {
        var markdown = File.ReadAllText(RepoPaths.ApplicationProtocolDoc);
        var section = markdown[markdown.IndexOf("## Server → browser", StringComparison.Ordinal)..];
        var result = new Dictionary<string, IReadOnlyList<DocumentedField>>(StringComparer.Ordinal);
        foreach (var line in section.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("| `", StringComparison.Ordinal)))
        {
            var cells = line.Split('|');
            var type = Backticked().Match(cells[1]).Groups["name"].Value;
            var fieldsCell = cells[2].Trim();
            var colonForm = ColonField().Match(fieldsCell);
            result[type] = colonForm.Success
                ? [new DocumentedField(colonForm.Groups["name"].Value, Names(colonForm.Groups["values"].Value))]
                : FieldItem().Matches(fieldsCell).Select(item => new DocumentedField(item.Groups["name"].Value, Names(item.Groups["detail"].Value))).ToList();
        }

        return result;
    }

    private static List<string> Names(string text) => Backticked().Matches(text).Select(match => match.Groups["name"].Value).ToList();

    [GeneratedRegex("`(?<name>[^`]+)`")]
    private static partial Regex Backticked();

    [GeneratedRegex("^`(?<name>[^`]+)`\\s*:\\s*(?<values>.+)$")]
    private static partial Regex ColonField();

    [GeneratedRegex("`(?<name>[^`]+)`(?:\\s*\\((?<detail>[^)]*)\\))?")]
    private static partial Regex FieldItem();
}

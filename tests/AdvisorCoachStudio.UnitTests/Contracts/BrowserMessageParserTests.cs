using System.Text;
using System.Text.RegularExpressions;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Contracts;

namespace AdvisorCoachStudio.UnitTests.Contracts;

/// <summary>Accepted browser messages (docs/application-protocol.md, "Browser → server").</summary>
public sealed partial class BrowserMessageParserTests
{
    private static readonly string TwentyMillisecondsOfPcm = Convert.ToBase64String(new byte[960]);

    [Fact]
    public void ParsesAudioAppend()
    {
        var message = Assert.IsType<AudioAppendMessage>(Parse($$"""{"v":1,"type":"audio.append","clientSequence":17,"audio":"{{TwentyMillisecondsOfPcm}}"}"""));

        Assert.Equal(17, message.ClientSequence);
        Assert.Equal(960, message.ByteLength);
        Assert.Equal(20, message.DurationMs);
        Assert.Equal(TwentyMillisecondsOfPcm, message.AudioBase64);
    }

    [Fact]
    public void AcceptsTheMaximumAudioChunk()
    {
        var audio = Convert.ToBase64String(new byte[AppProtocol.MaxAudioBytesPerMessage]);

        var message = Assert.IsType<AudioAppendMessage>(Parse($$"""{"v":1,"type":"audio.append","clientSequence":1,"audio":"{{audio}}"}"""));

        Assert.Equal(AppProtocol.MaxAudioBytesPerMessage, message.ByteLength);
        Assert.Equal(500, message.DurationMs);
    }

    [Fact]
    public void ParsesPlaybackProgress()
    {
        var message = Parse("""{"v":1,"type":"playback.progress","responseId":"resp_1","itemId":"item_1","contentIndex":0,"playedMilliseconds":1240}""");

        Assert.Equal(new PlaybackPositionMessage("resp_1", "item_1", 0, 1240, Stopped: false), message);
    }

    [Fact]
    public void ParsesPlaybackStopped()
    {
        var message = Parse("""{"v":1,"type":"playback.stopped","responseId":"resp_1","itemId":"item_1","contentIndex":2,"playedMilliseconds":980}""");

        Assert.Equal(new PlaybackPositionMessage("resp_1", "item_1", 2, 980, Stopped: true), message);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ParsesMicrophoneMuted(string json, bool muted) =>
        Assert.Equal(new MicrophoneMutedMessage(muted), Parse($$"""{"v":1,"type":"microphone.muted","muted":{{json}}}"""));

    [Theory]
    [InlineData("""{"v":1,"type":"session.end","reason":"user"}""", "user")]
    [InlineData("""{"v":1,"type":"session.end","reason":"navigation"}""", "navigation")]
    [InlineData("""{"v":1,"type":"session.end"}""", "user")]
    [InlineData("""{"v":1,"type":"session.end","reason":null}""", "user")]
    public void ParsesSessionEnd(string json, string reason) => Assert.Equal(new SessionEndMessage(reason), Parse(json));

    [Fact]
    public void ParsesHeartbeat() => Assert.IsType<HeartbeatMessage>(Parse("""{"v":1,"type":"heartbeat"}"""));

    [Fact]
    public void ControlMessagesAreAcceptedOnOutputOnlySockets()
    {
        Assert.IsType<HeartbeatMessage>(Parse("""{"v":1,"type":"heartbeat"}""", allowAudio: false));
        Assert.IsType<SessionEndMessage>(Parse("""{"v":1,"type":"session.end","reason":"user"}""", allowAudio: false));
    }

    [Fact]
    public void EveryDocumentedBrowserExampleParses()
    {
        var document = File.ReadAllText(RepoPaths.ApplicationProtocolDoc);
        var start = document.IndexOf("## Browser → server", StringComparison.Ordinal);
        var end = document.IndexOf("## Server → browser", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The protocol document must contain both message sections.");
        var examples = ExampleBlock().Matches(document[start..end])
            .Select(match => match.Groups["json"].Value.Replace("<base64 PCM16>", TwentyMillisecondsOfPcm, StringComparison.Ordinal))
            .ToList();

        var parsed = examples.Select(json => Parse(json).GetType()).ToList();

        Type[] expected = [typeof(AudioAppendMessage), typeof(PlaybackPositionMessage), typeof(PlaybackPositionMessage), typeof(MicrophoneMutedMessage), typeof(SessionEndMessage), typeof(HeartbeatMessage)];
        Assert.Equal(expected, parsed);
    }

    private static BrowserMessage Parse(string json, bool allowAudio = true) =>
        BrowserMessageParser.Parse(Encoding.UTF8.GetBytes(json), allowAudio);

    private static string RejectionCode(string json, bool allowAudio = true) =>
        Assert.Throws<AppProtocolException>(() => Parse(json, allowAudio)).Code;

    [GeneratedRegex("```json\\s*(?<json>\\{.*?\\})\\s*```", RegexOptions.Singleline)]
    private static partial Regex ExampleBlock();
}

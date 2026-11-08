using AdvisorCoachStudio.Web.Contracts;

namespace AdvisorCoachStudio.UnitTests.Contracts;

/// <summary>Rejected browser messages: every violation maps to a stable protocol error code.</summary>
public sealed partial class BrowserMessageParserTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"v":1,"type":"heartbeat" """)]
    [InlineData("""{"v":1,"type":"heartbeat","x":{"a":{"b":{"c":{"d":{}}}}}}""")]
    public void RejectsMalformedJson(string json) => Assert.Equal("malformed_message", RejectionCode(json));

    [Fact]
    public void RejectsTrailingDataAfterTheJsonObject()
    {
        Assert.Equal("malformed_message", RejectionCode("""{"v":1,"type":"heartbeat"} {"v":1,"type":"session.end"}"""));
        Assert.Equal("malformed_message", RejectionCode("""{"v":1,"type":"heartbeat"}garbage"""));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void RejectsNonObjectMessages(string json) => Assert.Equal("malformed_message", RejectionCode(json));

    [Theory]
    [InlineData("""{"type":"heartbeat"}""")]
    [InlineData("""{"v":2,"type":"heartbeat"}""")]
    [InlineData("""{"v":0,"type":"heartbeat"}""")]
    [InlineData("""{"v":"1","type":"heartbeat"}""")]
    [InlineData("""{"v":null,"type":"heartbeat"}""")]
    public void RejectsMissingOrWrongVersion(string json) => Assert.Equal("unsupported_version", RejectionCode(json));

    [Theory]
    [InlineData("transcript.final")]
    [InlineData("transcript.partial")]
    [InlineData("response.create")]
    [InlineData("session.update")]
    [InlineData("Heartbeat")]
    public void RejectsUnknownTypesIncludingTranscriptAndProviderCommands(string type) =>
        Assert.Equal("unknown_message_type", RejectionCode($$"""{"v":1,"type":"{{type}}","text":"synthetic"}"""));

    [Theory]
    [InlineData("""{"v":1}""")]
    [InlineData("""{"v":1,"type":""}""")]
    [InlineData("""{"v":1,"type":7}""")]
    public void RejectsMissingType(string json) => Assert.Equal("malformed_message", RejectionCode(json));

    [Fact]
    public void RejectsAudioThatIsNotBase64() => Assert.Equal("invalid_audio", RejectionCode(Audio("!!!not-base64!!!")));

    [Fact]
    public void RejectsAudioWithAnOddByteLength() => Assert.Equal("invalid_audio", RejectionCode(Audio(Convert.ToBase64String(new byte[961]))));

    [Fact]
    public void RejectsEmptyAudio()
    {
        Assert.Equal("malformed_message", RejectionCode(Audio("")));
        Assert.Equal("invalid_audio", RejectionCode(Audio("    ")));
    }

    [Fact]
    public void RejectsAudioLargerThanTheChunkLimit()
    {
        var oversized = Convert.ToBase64String(new byte[AppProtocol.MaxAudioBytesPerMessage + 2]);

        Assert.True(Audio(oversized).Length < AppProtocol.MaxMessageBytes, "The message itself must stay under the envelope limit.");
        Assert.Equal("invalid_audio", RejectionCode(Audio(oversized)));
    }

    [Fact]
    public void RejectsMessagesLargerThanTheAssembledLimit()
    {
        var audio = Convert.ToBase64String(new byte[24_600]);

        Assert.Equal("message_too_large", RejectionCode(Audio(audio)));
    }

    [Fact]
    public void RejectsControlMessagesLargerThanEightKilobytes()
    {
        var padding = new string('x', AppProtocol.MaxControlBytes);

        Assert.Equal("message_too_large", RejectionCode($$"""{"v":1,"type":"heartbeat","padding":"{{padding}}"}"""));
        Assert.Equal("message_too_large", RejectionCode($$"""{"v":1,"type":"session.end","reason":"user","padding":"{{padding}}"}"""));
    }

    [Fact]
    public void AcceptsControlMessagesOfExactlyEightKilobytes()
    {
        const string Envelope = """{"v":1,"type":"heartbeat","padding":""}""";
        var json = Envelope.Insert(Envelope.Length - 2, new string('x', AppProtocol.MaxControlBytes - Envelope.Length));

        Assert.Equal(AppProtocol.MaxControlBytes, json.Length);
        Assert.IsType<HeartbeatMessage>(Parse(json));
    }

    [Theory]
    [InlineData("""{"v":1,"type":"audio.append","audio":"AAAA"}""")]
    [InlineData("""{"v":1,"type":"audio.append","clientSequence":0,"audio":"AAAA"}""")]
    [InlineData("""{"v":1,"type":"audio.append","clientSequence":-3,"audio":"AAAA"}""")]
    [InlineData("""{"v":1,"type":"audio.append","clientSequence":"1","audio":"AAAA"}""")]
    [InlineData("""{"v":1,"type":"audio.append","clientSequence":1.5,"audio":"AAAA"}""")]
    public void RejectsInvalidClientSequence(string json) => Assert.Equal("invalid_sequence", RejectionCode(json));

    [Theory]
    [InlineData("""{"v":1,"type":"audio.append","clientSequence":1}""")]
    [InlineData("""{"v":1,"type":"playback.progress","itemId":"item_1","contentIndex":0,"playedMilliseconds":10}""")]
    [InlineData("""{"v":1,"type":"playback.progress","responseId":"resp_1","contentIndex":0,"playedMilliseconds":10}""")]
    [InlineData("""{"v":1,"type":"playback.progress","responseId":"resp_1","itemId":"item_1","playedMilliseconds":10}""")]
    [InlineData("""{"v":1,"type":"playback.stopped","responseId":"resp_1","itemId":"item_1","contentIndex":0}""")]
    [InlineData("""{"v":1,"type":"playback.progress","responseId":"","itemId":"item_1","contentIndex":0,"playedMilliseconds":10}""")]
    [InlineData("""{"v":1,"type":"microphone.muted"}""")]
    [InlineData("""{"v":1,"type":"microphone.muted","muted":"true"}""")]
    [InlineData("""{"v":1,"type":"session.end","reason":""}""")]
    [InlineData("""{"v":1,"type":"session.end","reason":5}""")]
    public void RejectsMissingOrMistypedFields(string json) => Assert.Equal("malformed_message", RejectionCode(json));

    [Theory]
    [InlineData("-1")]
    [InlineData("3600001")]
    [InlineData("2147483648")]
    [InlineData("12.5")]
    public void RejectsOutOfRangePlayedMilliseconds(string played) =>
        Assert.Equal("malformed_message", RejectionCode(Progress(played: played)));

    [Theory]
    [InlineData("-1")]
    [InlineData("65")]
    public void RejectsOutOfRangeContentIndex(string contentIndex) =>
        Assert.Equal("malformed_message", RejectionCode(Progress(contentIndex: contentIndex)));

    [Fact]
    public void AcceptsPlaybackBoundaries()
    {
        Assert.Equal(0, Assert.IsType<PlaybackPositionMessage>(Parse(Progress(played: "0"))).PlayedMilliseconds);
        Assert.Equal(3_600_000, Assert.IsType<PlaybackPositionMessage>(Parse(Progress(played: "3600000"))).PlayedMilliseconds);
        Assert.Equal(64, Assert.IsType<PlaybackPositionMessage>(Parse(Progress(contentIndex: "64"))).ContentIndex);
    }

    [Fact]
    public void RejectsOverlongIdentifiers()
    {
        var atLimit = new string('r', AppProtocol.MaxIdLength);
        var overLimit = new string('r', AppProtocol.MaxIdLength + 1);

        Assert.IsType<PlaybackPositionMessage>(Parse(Progress(responseId: atLimit)));
        Assert.Equal("malformed_message", RejectionCode(Progress(responseId: overLimit)));
    }

    [Fact]
    public void RejectsAudioOnOutputOnlySockets() =>
        Assert.Equal("audio_not_allowed", RejectionCode(Audio(TwentyMillisecondsOfPcm), allowAudio: false));

    private static string Audio(string base64) => $$"""{"v":1,"type":"audio.append","clientSequence":1,"audio":"{{base64}}"}""";

    private static string Progress(string responseId = "resp_1", string contentIndex = "0", string played = "100") =>
        $$"""{"v":1,"type":"playback.progress","responseId":"{{responseId}}","itemId":"item_1","contentIndex":{{contentIndex}},"playedMilliseconds":{{played}}}""";
}

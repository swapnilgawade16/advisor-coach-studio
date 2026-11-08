using System.Text;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

namespace AdvisorCoachStudio.UnitTests.AzureRealtime;

/// <summary>GA realtime server events parsed from checked-in synthetic fixtures (Fixtures/ga-events).</summary>
public sealed partial class ProviderEventParserTests
{
    [Fact]
    public void EveryFixtureParsesWithItsDeclaredType()
    {
        var files = Directory.GetFiles(RepoPaths.GaEventFixturesDirectory, "*.json");
        Assert.True(files.Length >= 22, $"Expected the synthetic GA fixtures in {RepoPaths.GaEventFixturesDirectory}.");

        foreach (var file in files)
        {
            var bytes = Fixture(Path.GetFileName(file));
            var declaredType = JsonNode.Parse(bytes)!["type"]!.GetValue<string>();

            var parsed = ProviderEventParser.Parse(bytes);

            Assert.Equal(declaredType, parsed.Type);
            Assert.Equal(!Path.GetFileName(file).StartsWith("legacy.", StringComparison.Ordinal), parsed.IsKnown);
            Assert.StartsWith("event_synthetic_", parsed.EventId);
        }
    }

    [Fact]
    public void ParsesSessionLifecycleEvents()
    {
        var created = Parse("session.created.json");
        var updated = Parse("session.updated.json");

        Assert.Equal("session.created", created.Type);
        Assert.Equal("sess_synthetic_0001", created.ProviderSessionId);
        Assert.Equal("event_synthetic_0001", created.EventId);
        Assert.Equal("session.updated", updated.Type);
        Assert.Equal("sess_synthetic_0001", updated.ProviderSessionId);
        Assert.Equal("Synthetic roleplay instructions.", updated.SessionInstructions);
    }

    [Fact]
    public void SessionInstructionsAreOptional()
    {
        var parsed = ProviderEventParser.Parse(Encoding.UTF8.GetBytes("""{"type":"session.updated","event_id":"event_1","session":{"id":"sess_1","instructions":null}}"""));

        Assert.Equal("sess_1", parsed.ProviderSessionId);
        Assert.Null(parsed.SessionInstructions);
    }

    [Fact]
    public void ParsesSpeechBoundaryAndCommitEvents()
    {
        var started = Parse("input_audio_buffer.speech_started.json");
        var stopped = Parse("input_audio_buffer.speech_stopped.json");
        var committed = Parse("input_audio_buffer.committed.json");

        Assert.Equal("item_synthetic_advisor_1", started.ItemId);
        Assert.Equal(1000, started.AudioStartMs);
        Assert.Equal("item_synthetic_advisor_1", stopped.ItemId);
        Assert.Equal(2000, stopped.AudioEndMs);
        Assert.Equal("item_synthetic_advisor_1", committed.ItemId);
        Assert.Equal("item_synthetic_client_0", committed.PreviousItemId);
    }

    [Fact]
    public void ParsesConversationItemAdded()
    {
        var added = Parse("conversation.item.added.json");

        Assert.Equal("item_synthetic_advisor_1", added.ItemId);
        Assert.Equal("item_synthetic_client_0", added.PreviousItemId);
        Assert.Equal("user", added.ItemRole);
        Assert.Equal("message", added.ItemType);
    }

    [Fact]
    public void ParsesInputTranscriptionEvents()
    {
        var delta = Parse("conversation.item.input_audio_transcription.delta.json");
        var completed = Parse("conversation.item.input_audio_transcription.completed.json");
        var failed = Parse("conversation.item.input_audio_transcription.failed.json");

        Assert.Equal("item_synthetic_advisor_1", delta.ItemId);
        Assert.Equal(0, delta.ContentIndex);
        Assert.Equal("I can hear that ", delta.Delta);
        Assert.Equal("I can hear that this has been a stressful morning for you.", completed.Transcript);
        Assert.Equal("event_synthetic_0008", completed.EventId);
        Assert.Equal("item_synthetic_advisor_3", failed.ItemId);
        Assert.Equal("transcription_error", failed.ErrorType);
        Assert.Equal("audio_unintelligible", failed.ErrorCode);
        Assert.Null(failed.Transcript);
    }

    [Fact]
    public void ParsesResponseCreatedAndOutputItemAdded()
    {
        var created = Parse("response.created.json");
        var item = Parse("response.output_item.added.json");

        Assert.Equal("resp_synthetic_1", created.ResponseId);
        Assert.Equal("in_progress", created.ResponseStatus);
        Assert.Equal("resp_synthetic_1", item.ResponseId);
        Assert.Equal("item_synthetic_client_2", item.ItemId);
        Assert.Equal("assistant", item.ItemRole);
    }

    [Fact]
    public void ParsesOutputAudioAndTranscriptEvents()
    {
        var audio = Parse("response.output_audio.delta.json");
        var audioDone = Parse("response.output_audio.done.json");
        var transcriptDelta = Parse("response.output_audio_transcript.delta.json");
        var transcriptDone = Parse("response.output_audio_transcript.done.json");

        Assert.Equal("resp_synthetic_1", audio.ResponseId);
        Assert.Equal("item_synthetic_client_2", audio.ItemId);
        Assert.Equal(new byte[8], Convert.FromBase64String(audio.Delta!));
        Assert.Equal("item_synthetic_client_2", audioDone.ItemId);
        Assert.Null(audioDone.Delta);
        Assert.Equal("Thank you for ", transcriptDelta.Delta);
        Assert.Equal("Thank you for asking. Retirement is what I am worried about.", transcriptDone.Transcript);
        Assert.Equal(0, transcriptDone.ContentIndex);
    }

    [Fact]
    public void ParsesCompletedResponseDoneWithOutputTranscripts()
    {
        var done = Parse("response.done.completed.json");

        Assert.Equal("resp_synthetic_1", done.ResponseId);
        Assert.Equal("completed", done.ResponseStatus);
        Assert.Null(done.ResponseStatusReason);
        Assert.Equal(
            new ProviderOutputPart("item_synthetic_client_2", 0, "assistant", "Thank you for asking. Retirement is what I am worried about."),
            Assert.Single(done.ResponseOutput));
        Assert.Equal(200, done.InputTokens);
        Assert.Equal(100, done.OutputTokens);
    }

    [Fact]
    public void ParsesCancelledResponseDoneWithTurnDetectedReason()
    {
        var done = Parse("response.done.cancelled-turn-detected.json");

        Assert.Equal("resp_synthetic_2", done.ResponseId);
        Assert.Equal("cancelled", done.ResponseStatus);
        Assert.Equal("turn_detected", done.ResponseStatusReason);
        var part = Assert.Single(done.ResponseOutput);
        Assert.Equal("item_synthetic_client_4", part.ItemId);
        Assert.Equal("I was going to say that I am not sure we", part.Transcript);
    }

    [Fact]
    public void ParsesErrorAndTruncatedEvents()
    {
        var error = Parse("error.json");
        var truncated = Parse("conversation.item.truncated.json");

        Assert.Equal("invalid_request_error", error.ErrorType);
        Assert.Equal("invalid_value", error.ErrorCode);
        Assert.Equal("item_synthetic_client_4", truncated.ItemId);
        Assert.Equal(1500, truncated.AudioEndMs);
    }

    [Fact]
    public void RateLimitsUpdatedIsAKnownIgnoredEvent()
    {
        var evt = Parse("rate_limits.updated.json");

        Assert.Equal("rate_limits.updated", evt.Type);
        Assert.True(evt.IsKnown);
        Assert.Null(evt.ResponseId);
        Assert.Null(evt.ItemId);
        Assert.Empty(evt.ResponseOutput);
    }

    [Theory]
    [InlineData("legacy.response.audio.delta.json", "response.audio.delta")]
    [InlineData("legacy.response.audio_transcript.delta.json", "response.audio_transcript.delta")]
    public void LegacyPreviewEventNamesAreNotKnown(string fixture, string type)
    {
        var evt = Parse(fixture);

        Assert.Equal(type, evt.Type);
        Assert.False(evt.IsKnown);
        Assert.Null(evt.Delta);
        Assert.Null(evt.ItemId);
    }

    [Fact]
    public void UnknownFutureEventsAreReportedAsUnknownWithoutThrowing()
    {
        var evt = ProviderEventParser.Parse(Encoding.UTF8.GetBytes("""{"type":"response.synthetic_future_event","event_id":"event_x","payload":{}}"""));

        Assert.False(evt.IsKnown);
        Assert.Equal("event_x", evt.EventId);
    }

    [Fact]
    public void EmptyFinalTranscriptsAreAllowed()
    {
        var root = JsonNode.Parse(Fixture("conversation.item.input_audio_transcription.completed.json"))!.AsObject();
        root["transcript"] = "";

        var evt = ProviderEventParser.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));

        Assert.Equal("", evt.Transcript);
    }

    private static ProviderEvent Parse(string fixture) => ProviderEventParser.Parse(Fixture(fixture));

    private static byte[] Fixture(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoPaths.GaEventFixturesDirectory, name));
        return bytes.AsSpan().StartsWith("\uFEFF"u8) ? bytes[3..] : bytes;
    }
}

using System.Text;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

namespace AdvisorCoachStudio.UnitTests.AzureRealtime;

/// <summary>Required-shape mismatches must fail visibly instead of producing empty audio or transcripts.</summary>
public sealed partial class ProviderEventParserTests
{
    public static TheoryData<string, string> RequiredFields => new()
    {
        { "session.created.json", "session" },
        { "error.json", "error" },
        { "input_audio_buffer.speech_started.json", "item_id" },
        { "input_audio_buffer.speech_started.json", "audio_start_ms" },
        { "input_audio_buffer.speech_stopped.json", "audio_end_ms" },
        { "input_audio_buffer.committed.json", "item_id" },
        { "conversation.item.added.json", "item" },
        { "conversation.item.added.json", "item.id" },
        { "conversation.item.input_audio_transcription.delta.json", "delta" },
        { "conversation.item.input_audio_transcription.completed.json", "transcript" },
        { "conversation.item.input_audio_transcription.completed.json", "content_index" },
        { "conversation.item.input_audio_transcription.failed.json", "item_id" },
        { "response.created.json", "response.id" },
        { "response.output_item.added.json", "response_id" },
        { "response.output_item.added.json", "item.id" },
        { "response.output_audio.delta.json", "response_id" },
        { "response.output_audio.delta.json", "delta" },
        { "response.output_audio.done.json", "item_id" },
        { "response.output_audio_transcript.delta.json", "content_index" },
        { "response.output_audio_transcript.done.json", "transcript" },
        { "response.done.completed.json", "response" },
        { "response.done.completed.json", "response.id" },
        { "response.done.completed.json", "response.status" },
        { "conversation.item.truncated.json", "audio_end_ms" },
    };

    [Theory]
    [MemberData(nameof(RequiredFields))]
    public void MissingRequiredFieldsThrowProviderProtocolException(string fixture, string path)
    {
        var root = JsonNode.Parse(Fixture(fixture))!.AsObject();
        var segments = path.Split('.');
        var parent = root;
        foreach (var segment in segments[..^1])
        {
            parent = parent[segment]!.AsObject();
        }

        Assert.True(parent.Remove(segments[^1]), $"Fixture {fixture} has no '{path}' to remove.");
        var expectedType = root["type"]!.GetValue<string>();

        var error = Assert.Throws<ProviderProtocolException>(() => ProviderEventParser.Parse(Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Equal(expectedType, error.EventType);
        Assert.Equal(path, error.Field);
    }

    [Theory]
    [InlineData("conversation.item.input_audio_transcription.completed.json", "content_index", "-1")]
    [InlineData("conversation.item.input_audio_transcription.completed.json", "content_index", "\"0\"")]
    [InlineData("input_audio_buffer.speech_started.json", "item_id", "\"\"")]
    [InlineData("input_audio_buffer.speech_started.json", "item_id", "42")]
    [InlineData("response.output_audio.delta.json", "delta", "null")]
    [InlineData("conversation.item.added.json", "item", "\"item_synthetic_advisor_1\"")]
    public void MistypedRequiredFieldsThrowProviderProtocolException(string fixture, string field, string replacementJson)
    {
        var root = JsonNode.Parse(Fixture(fixture))!.AsObject();
        root[field] = JsonNode.Parse(replacementJson);

        var error = Assert.Throws<ProviderProtocolException>(() => ProviderEventParser.Parse(Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.Equal(field, error.Field);
    }

    [Theory]
    [InlineData("""{"event_id":"event_x"}""")]
    [InlineData("""{"type":42}""")]
    [InlineData("[1,2]")]
    public void EventsWithoutATypeThrowProviderProtocolException(string json)
    {
        var error = Assert.Throws<ProviderProtocolException>(() => ProviderEventParser.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal("(unknown)", error.EventType);
        Assert.Equal("type", error.Field);
    }

    [Fact]
    public void ProtocolErrorsNeverEchoEventContent()
    {
        var root = JsonNode.Parse(Fixture("conversation.item.input_audio_transcription.completed.json"))!.AsObject();
        Assert.True(root.Remove("content_index"));

        var error = Assert.Throws<ProviderProtocolException>(() => ProviderEventParser.Parse(Encoding.UTF8.GetBytes(root.ToJsonString())));

        Assert.DoesNotContain("stressful morning", error.Message);
        Assert.DoesNotContain("item_synthetic_advisor_1", error.Message);
    }
}

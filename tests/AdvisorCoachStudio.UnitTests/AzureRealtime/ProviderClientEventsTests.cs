using System.Text.Json;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;

namespace AdvisorCoachStudio.UnitTests.AzureRealtime;

/// <summary>Client events sent to Azure use the GA shapes (spec section 9), never legacy preview fields.</summary>
public sealed class ProviderClientEventsTests
{
    private static readonly AzureRealtimeSessionSettings Settings = new("whisper-1", "alloy");

    [Fact]
    public void RoleplaySessionUpdateUsesTheGaSessionShape()
    {
        using var document = JsonDocument.Parse(ProviderClientEvents.SessionUpdateRoleplay("Synthetic instructions.", Settings, new RealtimeOptions()));
        var root = document.RootElement;
        var session = root.GetProperty("session");
        var input = session.GetProperty("audio").GetProperty("input");
        var vad = input.GetProperty("turn_detection");
        var output = session.GetProperty("audio").GetProperty("output");

        Assert.Equal("session.update", root.GetProperty("type").GetString());
        Assert.Equal("realtime", session.GetProperty("type").GetString());
        Assert.Equal("Synthetic instructions.", session.GetProperty("instructions").GetString());
        Assert.Equal(new[] { "audio" }, session.GetProperty("output_modalities").EnumerateArray().Select(m => m.GetString()));
        AssertPcm24k(input.GetProperty("format"));
        Assert.Equal("whisper-1", input.GetProperty("transcription").GetProperty("model").GetString());
        Assert.Equal("server_vad", vad.GetProperty("type").GetString());
        Assert.Equal(0.5, vad.GetProperty("threshold").GetDouble());
        Assert.Equal(300, vad.GetProperty("prefix_padding_ms").GetInt32());
        Assert.Equal(500, vad.GetProperty("silence_duration_ms").GetInt32());
        Assert.True(vad.GetProperty("create_response").GetBoolean());
        Assert.True(vad.GetProperty("interrupt_response").GetBoolean());
        AssertPcm24k(output.GetProperty("format"));
        Assert.Equal("alloy", output.GetProperty("voice").GetString());
    }

    [Fact]
    public void RoleplaySessionUpdateHasNoLegacyPreviewFields()
    {
        using var document = JsonDocument.Parse(ProviderClientEvents.SessionUpdateRoleplay("Synthetic instructions.", Settings, new RealtimeOptions()));
        var root = document.RootElement;
        var session = root.GetProperty("session");

        Assert.Equal(new[] { "event_id", "session", "type" }, PropertyNames(root));
        Assert.Equal(new[] { "audio", "instructions", "output_modalities", "type" }, PropertyNames(session));
        Assert.Equal(new[] { "format", "transcription", "turn_detection" }, PropertyNames(session.GetProperty("audio").GetProperty("input")));
    }

    [Fact]
    public void RoleplaySessionUpdateTakesVadTuningFromConfiguration()
    {
        var realtime = new RealtimeOptions { VadThreshold = 0.65, VadPrefixPaddingMs = 250, VadSilenceDurationMs = 700 };

        using var document = JsonDocument.Parse(ProviderClientEvents.SessionUpdateRoleplay("Synthetic.", Settings, realtime));
        var vad = document.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("turn_detection");

        Assert.Equal(0.65, vad.GetProperty("threshold").GetDouble());
        Assert.Equal(250, vad.GetProperty("prefix_padding_ms").GetInt32());
        Assert.Equal(700, vad.GetProperty("silence_duration_ms").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void VoiceIsOmittedWhenNotConfigured(string? voice)
    {
        using var document = JsonDocument.Parse(ProviderClientEvents.SessionUpdateRoleplay("Synthetic.", new AzureRealtimeSessionSettings("whisper-1", voice), new RealtimeOptions()));
        var output = document.RootElement.GetProperty("session").GetProperty("audio").GetProperty("output");

        Assert.Equal(new[] { "format" }, PropertyNames(output));
    }

    [Fact]
    public void DemonstrationSessionUpdateDisablesTurnDetectionAndTranscription()
    {
        using var document = JsonDocument.Parse(ProviderClientEvents.SessionUpdateDemonstration("Synthetic demonstration.", Settings));
        var session = document.RootElement.GetProperty("session");
        var input = session.GetProperty("audio").GetProperty("input");

        Assert.Equal("session.update", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("realtime", session.GetProperty("type").GetString());
        Assert.Equal(new[] { "audio" }, session.GetProperty("output_modalities").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
        Assert.False(input.TryGetProperty("transcription", out _));
        AssertPcm24k(input.GetProperty("format"));
        Assert.Equal("alloy", session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
    }

    [Fact]
    public void TruncateUsesItemContentAndPlayedPosition()
    {
        using var document = JsonDocument.Parse(ProviderClientEvents.Truncate("item_synthetic_client_4", 0, 980));
        var root = document.RootElement;

        Assert.Equal(new[] { "audio_end_ms", "content_index", "event_id", "item_id", "type" }, PropertyNames(root));
        Assert.Equal("conversation.item.truncate", root.GetProperty("type").GetString());
        Assert.Equal("item_synthetic_client_4", root.GetProperty("item_id").GetString());
        Assert.Equal(0, root.GetProperty("content_index").GetInt32());
        Assert.Equal(980, root.GetProperty("audio_end_ms").GetInt32());
    }

    [Fact]
    public void ResponseCancelTargetsAResponseOnlyWhenKnown()
    {
        using var targeted = JsonDocument.Parse(ProviderClientEvents.ResponseCancel("resp_synthetic_2"));
        using var untargeted = JsonDocument.Parse(ProviderClientEvents.ResponseCancel(null));

        Assert.Equal("response.cancel", targeted.RootElement.GetProperty("type").GetString());
        Assert.Equal("resp_synthetic_2", targeted.RootElement.GetProperty("response_id").GetString());
        Assert.Equal(new[] { "event_id", "type" }, PropertyNames(untargeted.RootElement));
    }

    [Fact]
    public void AppendCommitAndCreateUseTheirExactShapes()
    {
        var audio = Convert.ToBase64String(new byte[960]);
        using var append = JsonDocument.Parse(ProviderClientEvents.AppendAudio(audio));
        using var commit = JsonDocument.Parse(ProviderClientEvents.CommitAudio());
        using var create = JsonDocument.Parse(ProviderClientEvents.ResponseCreate());

        Assert.Equal(new[] { "audio", "event_id", "type" }, PropertyNames(append.RootElement));
        Assert.Equal("input_audio_buffer.append", append.RootElement.GetProperty("type").GetString());
        Assert.Equal(audio, append.RootElement.GetProperty("audio").GetString());
        Assert.Equal(new[] { "event_id", "type" }, PropertyNames(commit.RootElement));
        Assert.Equal("input_audio_buffer.commit", commit.RootElement.GetProperty("type").GetString());
        Assert.Equal(new[] { "event_id", "type" }, PropertyNames(create.RootElement));
        Assert.Equal("response.create", create.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void EveryClientEventGetsAUniqueEventId()
    {
        using var first = JsonDocument.Parse(ProviderClientEvents.ResponseCreate());
        using var second = JsonDocument.Parse(ProviderClientEvents.ResponseCreate());
        var firstId = first.RootElement.GetProperty("event_id").GetString();

        Assert.StartsWith("evt_", firstId);
        Assert.NotEqual(firstId, second.RootElement.GetProperty("event_id").GetString());
    }

    private static void AssertPcm24k(JsonElement format)
    {
        Assert.Equal(new[] { "rate", "type" }, PropertyNames(format));
        Assert.Equal("audio/pcm", format.GetProperty("type").GetString());
        Assert.Equal(24_000, format.GetProperty("rate").GetInt32());
    }

    private static List<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToList();
}

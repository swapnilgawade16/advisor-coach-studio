using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Sessions;

namespace AdvisorCoachStudio.UnitTests.Sessions;

/// <summary>Ordering, deduplication, and response.done fallback rules (spec section 10.2).</summary>
public sealed partial class TranscriptAssemblerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly Guid _sessionId = Guid.NewGuid();

    [Fact]
    public void AdvisorTranscriptionCompletingAfterTheClientReplyKeepsConversationOrder()
    {
        var assembler = NewAssembler();
        ClientTurn(assembler, "resp_0", "c0", previousItemId: null, "Opening line from the client.");
        AdvisorCommitted(assembler, "a1", previousItemId: "c0");
        ClientTurn(assembler, "resp_1", "c1", previousItemId: "a1", "Reply from the client.");
        _time.Advance(TimeSpan.FromSeconds(2));

        var update = assembler.OnInputTranscriptionCompleted("a1", 0, "  Advisor words that finished late.  ", "evt_a1");
        var frozen = assembler.Freeze();

        Assert.NotNull(update);
        Assert.True(update.IsFinal);
        Assert.Equal(2, update.Sequence);
        Assert.Equal("Advisor words that finished late.", update.Text);
        Assert.Equal(new[] { "c0", "a1", "c1" }, frozen.Turns.Select(turn => turn.ProviderItemId));
        Assert.Equal(new[] { TurnSpeaker.Client, TurnSpeaker.Advisor, TurnSpeaker.Client }, frozen.Turns.Select(turn => turn.Speaker));
        Assert.Equal(new[] { 1, 2, 3 }, frozen.Turns.Select(turn => turn.Sequence));
        Assert.True(frozen.Complete);
    }

    [Fact]
    public void AdvisorCommitWithPreviousItemIdIsPlacedBeforeEarlierArrivingClientOutput()
    {
        var assembler = NewAssembler();
        ClientTurn(assembler, "resp_0", "c0", previousItemId: null, "Opening line.");
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);
        assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "Client reply.", "evt_c1");

        assembler.OnInputCommitted("a1", "c0");
        assembler.OnItemAdded("a1", "c0", "user");
        assembler.OnItemAdded("c1", "a1", "assistant");
        assembler.OnResponseDone("resp_1", "completed", null, [("c1", 0, "Client reply.")]);
        assembler.OnInputTranscriptionCompleted("a1", 0, "Advisor words.", "evt_a1");

        Assert.Equal(new[] { "c0", "a1", "c1" }, assembler.Freeze().Turns.Select(turn => turn.ProviderItemId));
    }

    [Fact]
    public void AuthoritativeItemAddedMovesAFallbackPlacedClientItem()
    {
        var assembler = NewAssembler();
        ClientTurn(assembler, "resp_0", "c0", previousItemId: null, "Opening line.");
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);
        assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "Client reply.", "evt_c1");
        assembler.OnInputCommitted("a1", previousItemId: null);

        assembler.OnItemAdded("c1", "a1", "assistant");
        assembler.OnInputTranscriptionCompleted("a1", 0, "Advisor words.", "evt_a1");

        Assert.Equal(new[] { "c0", "a1", "c1" }, assembler.Freeze().Turns.Select(turn => turn.ProviderItemId));
    }

    [Fact]
    public void DuplicateFinalEventsAreIgnoredAndCounted()
    {
        var assembler = NewAssembler();
        AdvisorCommitted(assembler, "a1", previousItemId: null);
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);

        Assert.NotNull(assembler.OnInputTranscriptionCompleted("a1", 0, "First final text.", "evt_first"));
        Assert.Null(assembler.OnInputTranscriptionCompleted("a1", 0, "Second final text.", "evt_second"));
        Assert.Null(assembler.OnInputTranscriptionDelta("a1", 0, " extra"));
        Assert.Null(assembler.OnInputTranscriptionFailed("a1", 0, "evt_failed"));
        Assert.NotNull(assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "Client text.", "evt_client"));
        Assert.Null(assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "Client text, repeated.", "evt_client_again"));
        Assert.Null(assembler.OnOutputTranscriptDelta("resp_1", "c1", 0, " more"));

        Assert.Equal(5, assembler.DuplicateEventCount);
        var turns = assembler.Freeze().Turns;
        var advisor = Assert.Single(turns, turn => turn.Speaker == TurnSpeaker.Advisor);
        Assert.Equal("First final text.", advisor.Text);
        Assert.Equal("evt_first", advisor.SourceEventId);
        Assert.Equal(TurnStatus.Final, advisor.Status);
        Assert.Equal("Client text.", Assert.Single(turns, turn => turn.Speaker == TurnSpeaker.Client).Text);
    }

    [Fact]
    public void ResponseDoneTranscriptIsNeverConcatenatedWithTheFinalTranscript()
    {
        var assembler = NewAssembler();
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);
        assembler.OnOutputTranscriptDelta("resp_1", "c1", 0, "Hello");
        assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "Hello there.", "evt_done");

        var updates = assembler.OnResponseDone("resp_1", "completed", null, [("c1", 0, "Hello there, friend.")]);

        Assert.Empty(updates);
        Assert.Equal("Hello there.", Assert.Single(assembler.Freeze().Turns).Text);
    }

    [Fact]
    public void ResponseDoneTranscriptIsUsedOnlyAsAFallback()
    {
        var assembler = NewAssembler();
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);
        assembler.OnOutputTranscriptDelta("resp_1", "c1", 0, "Full fall");

        var update = Assert.Single(assembler.OnResponseDone("resp_1", "completed", null, [("c1", 0, "Full fallback text.")]));

        Assert.True(update.IsFinal);
        Assert.Equal(TurnStatus.Final, update.Status);
        Assert.Equal("Full fallback text.", update.Text);
        var turn = Assert.Single(assembler.Freeze().Turns);
        Assert.Equal("Full fallback text.", turn.Text);
        Assert.Equal(TurnStatus.Final, turn.Status);
    }

    private TranscriptAssembler NewAssembler() => new(_sessionId, _time);

    /// <summary>A complete client response whose audio was fully played.</summary>
    private static void ClientTurn(TranscriptAssembler assembler, string responseId, string itemId, string? previousItemId, string text)
    {
        assembler.OnOutputItemAdded(responseId, itemId);
        assembler.OnItemAdded(itemId, previousItemId, "assistant");
        assembler.OnOutputAudio(responseId, itemId, 0);
        assembler.OnOutputTranscriptDelta(responseId, itemId, 0, text[..Math.Min(5, text.Length)]);
        assembler.OnOutputTranscriptDone(responseId, itemId, 0, text, "evt_" + itemId);
        assembler.OnResponseDone(responseId, "completed", null, [(itemId, 0, text)]);
        assembler.MarkPlaybackComplete(itemId, 0);
    }

    /// <summary>Advisor speech that server VAD detected and committed (transcription still outstanding).</summary>
    private static void AdvisorCommitted(TranscriptAssembler assembler, string itemId, string? previousItemId)
    {
        assembler.OnSpeechStarted(itemId);
        assembler.OnSpeechStopped(itemId);
        assembler.OnInputCommitted(itemId, previousItemId);
        assembler.OnItemAdded(itemId, previousItemId, "user");
    }
}

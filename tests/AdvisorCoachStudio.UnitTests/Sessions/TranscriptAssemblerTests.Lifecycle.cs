using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.UnitTests.Sessions;

/// <summary>Interruption, failure, pending-work, and freeze rules (spec sections 8.5, 10.2, and 10.3).</summary>
public sealed partial class TranscriptAssemblerTests
{
    [Fact]
    public void InterruptedClientTurnIsNotFullyHeardAndNotQuotable()
    {
        var assembler = NewAssembler();
        ClientTurn(assembler, "resp_0", "c0", previousItemId: null, "Opening line.");
        AdvisorCommitted(assembler, "a1", previousItemId: "c0");
        assembler.OnInputTranscriptionCompleted("a1", 0, "What is worrying you most?", "evt_a1");
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnItemAdded("c1", "a1", "assistant");
        assembler.OnOutputAudio("resp_1", "c1", 0);
        assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "I was going to say that I am not sure we", "evt_c1");
        assembler.OnSpeechStarted("a2");

        var update = assembler.MarkNotFullyHeard("c1", 0);

        Assert.NotNull(update);
        Assert.True(update.IsFinal);
        Assert.Equal(TurnStatus.Interrupted, update.Status);
        Assert.False(update.FullyHeard);
        Assert.Null(assembler.MarkNotFullyHeard("c1", 0));
        Assert.Null(assembler.MarkNotFullyHeard("a1", 0));
        Assert.Empty(assembler.OnResponseDone("resp_1", "cancelled", "turn_detected", [("c1", 0, "I was going to say that I am not sure we")]));
        var frozen = assembler.Freeze();
        var interrupted = Assert.Single(frozen.Turns, turn => turn.ProviderItemId == "c1");
        Assert.Equal(TurnStatus.Interrupted, interrupted.Status);
        Assert.False(interrupted.FullyHeard);
        Assert.False(interrupted.IsQuotable);
        Assert.Equal("I was going to say that I am not sure we", interrupted.Text);
        var opening = Assert.Single(frozen.Turns, turn => turn.ProviderItemId == "c0");
        Assert.True(opening.FullyHeard);
        Assert.True(opening.IsQuotable);
        Assert.DoesNotContain(frozen.Turns, turn => turn.ProviderItemId == "a2");
        Assert.True(frozen.Complete);
    }

    [Fact]
    public void TurnDetectedCancellationMarksEveryUnplayedTurnOfTheResponse()
    {
        var assembler = NewAssembler();
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);
        assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "First part that was fully played.", "evt_c1");
        assembler.MarkPlaybackComplete("c1", 0);
        assembler.OnOutputItemAdded("resp_1", "c2");
        assembler.OnOutputAudio("resp_1", "c2", 0);
        assembler.OnOutputTranscriptDone("resp_1", "c2", 0, "Second part that was cut off.", "evt_c2");

        var update = Assert.Single(assembler.OnResponseDone("resp_1", "cancelled", "turn_detected", []));

        Assert.Equal("c2", update.ItemId);
        Assert.Equal(TurnStatus.Interrupted, update.Status);
        Assert.False(update.FullyHeard);
        var turns = assembler.Freeze().Turns;
        Assert.True(Assert.Single(turns, turn => turn.ProviderItemId == "c1").IsQuotable);
        var cutOff = Assert.Single(turns, turn => turn.ProviderItemId == "c2");
        Assert.Equal(TurnStatus.Interrupted, cutOff.Status);
        Assert.False(cutOff.IsQuotable);
    }

    [Fact]
    public void MarkResponseNotFullyHeardCoversTranscriptOnlyTurnsAndOnlyThatResponse()
    {
        var assembler = NewAssembler();
        assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "Transcript that was never played.", "evt_c1");
        assembler.OnOutputAudio("resp_2", "c2", 0);
        assembler.OnOutputTranscriptDone("resp_2", "c2", 0, "Another response.", "evt_c2");

        var updates = assembler.MarkResponseNotFullyHeard("resp_1");

        Assert.Equal("c1", Assert.Single(updates).ItemId);
        Assert.Empty(assembler.MarkResponseNotFullyHeard("resp_1"));
        var turns = assembler.Freeze().Turns;
        Assert.Equal(TurnStatus.Interrupted, Assert.Single(turns, turn => turn.ProviderItemId == "c1").Status);
        var unconfirmed = Assert.Single(turns, turn => turn.ProviderItemId == "c2");
        Assert.Equal(TurnStatus.Final, unconfirmed.Status);
        Assert.Null(unconfirmed.FullyHeard);
        Assert.False(unconfirmed.IsQuotable);
    }

    [Fact]
    public void TurnDetectedCancellationUsesTheListedTranscriptAsInterruptedText()
    {
        var assembler = NewAssembler();
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);
        assembler.OnOutputTranscriptDelta("resp_1", "c1", 0, "I was going");

        var update = Assert.Single(assembler.OnResponseDone("resp_1", "cancelled", "turn_detected", [("c1", 0, "I was going to say")]));

        Assert.Equal(TurnStatus.Interrupted, update.Status);
        Assert.False(update.FullyHeard);
        var turn = Assert.Single(assembler.Freeze().Turns);
        Assert.Equal("I was going to say", turn.Text);
        Assert.False(turn.IsQuotable);
    }

    [Fact]
    public void FailedAdvisorTranscriptionIsPreservedAndMakesTheTranscriptIncomplete()
    {
        var assembler = NewAssembler();
        AdvisorCommitted(assembler, "a1", previousItemId: null);

        var update = assembler.OnInputTranscriptionFailed("a1", 0, "evt_failed");

        Assert.NotNull(update);
        Assert.True(update.IsFinal);
        Assert.Equal(TurnStatus.Failed, update.Status);
        Assert.False(assembler.HasPendingWork);
        var frozen = assembler.Freeze();
        var turn = Assert.Single(frozen.Turns);
        Assert.Equal(TurnStatus.Failed, turn.Status);
        Assert.Equal("", turn.Text);
        Assert.Equal("evt_failed", turn.SourceEventId);
        Assert.False(turn.IsQuotable);
        Assert.False(frozen.Complete);
        Assert.Equal(0, assembler.FinalAdvisorTurnCount);
    }

    [Fact]
    public void ClientAudioWithoutAnyTranscriptIsAFailedCaption()
    {
        var assembler = NewAssembler();
        assembler.OnOutputItemAdded("resp_1", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);

        var update = Assert.Single(assembler.OnResponseDone("resp_1", "completed", null, [("c1", 0, null)]));

        Assert.Equal(TurnStatus.Failed, update.Status);
        var frozen = assembler.Freeze();
        Assert.Equal(TurnStatus.Failed, Assert.Single(frozen.Turns).Status);
        Assert.False(frozen.Complete);
    }

    [Fact]
    public void LateEventsAfterFreezeAreIgnoredAndCounted()
    {
        var assembler = NewAssembler();
        ClientTurn(assembler, "resp_0", "c0", previousItemId: null, "Opening line.");
        AdvisorCommitted(assembler, "a1", previousItemId: "c0");
        var frozen = assembler.Freeze();

        Assert.Null(assembler.OnInputTranscriptionCompleted("a1", 0, "Too late.", "evt_late"));
        Assert.Null(assembler.OnInputTranscriptionDelta("a1", 0, "Too"));
        Assert.Null(assembler.OnInputTranscriptionFailed("a1", 0, "evt_late_failure"));
        Assert.Null(assembler.OnOutputTranscriptDelta("resp_1", "c1", 0, "Late"));
        Assert.Null(assembler.OnOutputTranscriptDone("resp_1", "c1", 0, "Late reply.", "evt_late_reply"));
        Assert.Empty(assembler.OnResponseDone("resp_1", "completed", null, [("c1", 0, "Late reply.")]));
        assembler.OnItemAdded("c1", "a1", "assistant");
        assembler.OnInputCommitted("a2", "c1");
        assembler.OnOutputAudio("resp_1", "c1", 0);
        assembler.OnSpeechStarted("a3");
        Assert.Null(assembler.MarkNotFullyHeard("c0", 0));
        Assert.Empty(assembler.MarkResponseNotFullyHeard("resp_0"));

        Assert.True(assembler.IsFrozen);
        Assert.Equal(8, assembler.LateEventCount);
        Assert.Equal(frozen.Turns, assembler.Freeze().Turns);
        Assert.Equal(TurnStatus.Pending, Assert.Single(frozen.Turns, turn => turn.ProviderItemId == "a1").Status);
        Assert.False(frozen.Complete);
    }

    [Fact]
    public void HasPendingWorkTracksCommittedAdvisorSpeechAndUnfinishedResponses()
    {
        var assembler = NewAssembler();
        Assert.False(assembler.HasPendingWork);

        assembler.OnSpeechStarted("a1");
        Assert.False(assembler.HasPendingWork);
        assembler.OnInputCommitted("a1", previousItemId: null);
        Assert.True(assembler.HasPendingWork);
        assembler.OnInputTranscriptionDelta("a1", 0, "Partial");
        Assert.True(assembler.HasPendingWork);
        assembler.OnInputTranscriptionCompleted("a1", 0, "Partial words, now final.", "evt_a1");
        Assert.False(assembler.HasPendingWork);

        assembler.OnOutputAudio("resp_1", "c1", 0);
        Assert.True(assembler.HasPendingWork);
        assembler.OnResponseDone("resp_1", "completed", null, [("c1", 0, "Reply.")]);
        Assert.False(assembler.HasPendingWork);
    }

    [Fact]
    public void EmptyFinalAdvisorTranscriptIsDroppedAndSequencesStayContiguous()
    {
        var assembler = NewAssembler();
        ClientTurn(assembler, "resp_0", "c0", previousItemId: null, "Opening line.");
        AdvisorCommitted(assembler, "a1", previousItemId: "c0");
        assembler.OnInputTranscriptionCompleted("a1", 0, "   ", "evt_empty");
        assembler.OnSpeechStarted("noise");
        ClientTurn(assembler, "resp_1", "c1", previousItemId: "a1", "Are you still there?");
        AdvisorCommitted(assembler, "a2", previousItemId: "c1");
        assembler.OnInputTranscriptionCompleted("a2", 0, "Yes, sorry. Let's continue.", "evt_a2");

        var frozen = assembler.Freeze();

        Assert.Equal(new[] { "c0", "c1", "a2" }, frozen.Turns.Select(turn => turn.ProviderItemId));
        Assert.Equal(Enumerable.Range(1, frozen.Turns.Count), frozen.Turns.Select(turn => turn.Sequence));
        Assert.All(frozen.Turns, turn => Assert.Equal(_sessionId, turn.SessionId));
        Assert.True(frozen.Complete);
        Assert.Equal(1, assembler.FinalAdvisorTurnCount);
    }

    [Fact]
    public void TimestampsComeFromTheInjectedClock()
    {
        var assembler = NewAssembler();
        var start = _time.GetUtcNow().UtcDateTime;

        assembler.OnSpeechStarted("a1");
        _time.Advance(TimeSpan.FromSeconds(3));
        assembler.OnSpeechStopped("a1");
        assembler.OnInputCommitted("a1", previousItemId: null);
        assembler.OnInputTranscriptionCompleted("a1", 0, "Timed words.", "evt_a1");

        var turn = Assert.Single(assembler.Freeze().Turns);
        Assert.Equal(start, turn.CreatedAtUtc);
        Assert.Equal(start, turn.StartedAtUtc);
        Assert.Equal(start.AddSeconds(3), turn.EndedAtUtc);
    }
}

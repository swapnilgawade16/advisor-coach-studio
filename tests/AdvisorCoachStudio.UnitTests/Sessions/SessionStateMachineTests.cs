using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.UnitTests.Sessions;

public sealed class SessionStateMachineTests
{
    /// <summary>Spec section 13.1.</summary>
    private static readonly (ConversationState From, ConversationState To)[] Allowed =
    [
        (ConversationState.Created, ConversationState.Connecting),
        (ConversationState.Created, ConversationState.Failed),
        (ConversationState.Created, ConversationState.Expired),
        (ConversationState.Connecting, ConversationState.Active),
        (ConversationState.Connecting, ConversationState.Failed),
        (ConversationState.Active, ConversationState.Ending),
        (ConversationState.Ending, ConversationState.TranscriptFinalizing),
        (ConversationState.TranscriptFinalizing, ConversationState.Ended),
    ];

    public static TheoryData<ConversationState, ConversationState> AllowedTransitions
    {
        get
        {
            var data = new TheoryData<ConversationState, ConversationState>();
            foreach (var (from, to) in Allowed)
            {
                data.Add(from, to);
            }

            return data;
        }
    }

    public static TheoryData<ConversationState, ConversationState> ForbiddenTransitions
    {
        get
        {
            var data = new TheoryData<ConversationState, ConversationState>();
            foreach (var from in Enum.GetValues<ConversationState>())
            {
                foreach (var to in Enum.GetValues<ConversationState>().Where(to => !Allowed.Contains((from, to))))
                {
                    data.Add(from, to);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void AllowsDocumentedTransitions(ConversationState from, ConversationState to) =>
        Assert.True(SessionStateMachine.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(ForbiddenTransitions))]
    public void ForbidsEveryOtherTransition(ConversationState from, ConversationState to) =>
        Assert.False(SessionStateMachine.CanTransition(from, to));

    [Fact]
    public void ForbiddenSetCoversSelfLoopsBackwardsMovesAndTerminalStates()
    {
        var forbidden = ForbiddenTransitions;

        Assert.Equal((8 * 8) - Allowed.Length, forbidden.Count);
        Assert.False(SessionStateMachine.CanTransition(ConversationState.Active, ConversationState.Active));
        Assert.False(SessionStateMachine.CanTransition(ConversationState.Active, ConversationState.Connecting));
        Assert.False(SessionStateMachine.CanTransition(ConversationState.Active, ConversationState.Ended));
        Assert.False(SessionStateMachine.CanTransition(ConversationState.Active, ConversationState.Failed));
        Assert.False(SessionStateMachine.CanTransition(ConversationState.Ended, ConversationState.Ended));
        Assert.False(SessionStateMachine.CanTransition(ConversationState.Expired, ConversationState.Connecting));
    }

    [Theory]
    [InlineData(ConversationState.Created, false, false)]
    [InlineData(ConversationState.Connecting, true, false)]
    [InlineData(ConversationState.Active, true, false)]
    [InlineData(ConversationState.Ending, true, false)]
    [InlineData(ConversationState.TranscriptFinalizing, true, false)]
    [InlineData(ConversationState.Ended, false, true)]
    [InlineData(ConversationState.Failed, false, true)]
    [InlineData(ConversationState.Expired, false, true)]
    public void ClassifiesLiveAndTerminalStates(ConversationState state, bool live, bool terminal)
    {
        Assert.Equal(live, SessionStateMachine.IsLive(state));
        Assert.Equal(terminal, SessionStateMachine.IsTerminal(state));
    }
}

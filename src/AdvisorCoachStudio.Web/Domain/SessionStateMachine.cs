namespace AdvisorCoachStudio.Web.Domain;

/// <summary>Allowed conversation-state transitions (spec section 13.1). Enforced server-side.</summary>
public static class SessionStateMachine
{
    private static readonly Dictionary<ConversationState, ConversationState[]> Allowed = new()
    {
        [ConversationState.Created] = [ConversationState.Connecting, ConversationState.Failed, ConversationState.Expired],
        [ConversationState.Connecting] = [ConversationState.Active, ConversationState.Failed],
        [ConversationState.Active] = [ConversationState.Ending],
        [ConversationState.Ending] = [ConversationState.TranscriptFinalizing],
        [ConversationState.TranscriptFinalizing] = [ConversationState.Ended],
        [ConversationState.Ended] = [],
        [ConversationState.Failed] = [],
        [ConversationState.Expired] = [],
    };

    public static bool CanTransition(ConversationState from, ConversationState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static bool IsLive(ConversationState state) =>
        state is ConversationState.Connecting or ConversationState.Active or ConversationState.Ending or ConversationState.TranscriptFinalizing;

    public static bool IsTerminal(ConversationState state) =>
        state is ConversationState.Ended or ConversationState.Failed or ConversationState.Expired;
}

namespace AdvisorCoachStudio.Web.Domain;

public enum AiMode
{
    Unconfigured,
    Azure,
    DevelopmentSimulation,
}

public enum Difficulty
{
    Beginner,
    Intermediate,
    Advanced,
}

public enum ConversationState
{
    Created,
    Connecting,
    Active,
    Ending,
    TranscriptFinalizing,
    Ended,
    Failed,
    Expired,
}

public enum SessionEndReason
{
    None,
    UserEnded,
    DurationLimit,
    Disconnected,
    ProviderError,
    ConnectionQuality,
    AccessExpired,

    /// <summary>Demonstrations only: the example finished playing.</summary>
    Completed,
}

public enum TranscriptStatus
{
    NotFinalized,
    Complete,
    Incomplete,
}

public enum ReportStatus
{
    NotRequested,
    SetupRequired,
    Queued,
    Generating,
    Ready,
    Failed,
}

public enum TurnSpeaker
{
    Advisor,
    Client,
}

public enum TurnStatus
{
    Pending,
    Final,
    Failed,
    Interrupted,
}

public enum TurnSource
{
    InputTranscription,
    OutputAudioTranscript,
}

public enum DemonstrationStatus
{
    Created,
    Connecting,
    Playing,
    Completed,
    Stopped,
    Failed,
}

public enum AssessmentStatus
{
    SufficientEvidence,
    LimitedEvidence,
}

public enum ProviderReadinessStatus
{
    Ready,
    SetupRequired,
    Unavailable,
}

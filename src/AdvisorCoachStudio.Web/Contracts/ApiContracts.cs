using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Sessions;

namespace AdvisorCoachStudio.Web.Contracts;

public sealed record AccessCodeRequest(string? Code);

public sealed record CreateSessionRequest(string? ScenarioId, string? Difficulty, bool AcknowledgedNotice);

public sealed record CreateSessionResponse(Guid SessionId, string PracticeUrl, string WebSocketPath, string ReportUrl, int MaxDurationSeconds, bool Simulated);

public sealed record CapabilityDto(ProviderReadinessStatus Status, string Code, string Message);

public sealed record CapabilitiesResponse(
    AiMode Mode,
    CapabilityDto Realtime,
    CapabilityDto Reports,
    bool DemonstrationAvailable,
    bool Simulated,
    string? SimulationLabel,
    int MaxSessionMinutes,
    string DataNotice);

public sealed record SessionStatusResponse(
    Guid SessionId,
    string ScenarioId,
    string ScenarioVersion,
    string ScenarioTitle,
    string ClientDisplayName,
    string ClientInitials,
    Difficulty Difficulty,
    ConversationState State,
    SessionEndReason EndReason,
    TranscriptStatus TranscriptStatus,
    ReportStatus ReportStatus,
    string? ReportErrorCode,
    bool Simulated,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc,
    string WebSocketPath,
    string ReportUrl)
{
    public static SessionStatusResponse From(SessionSnapshot s) => new(
        s.Id, s.ScenarioId, s.ScenarioVersion, s.ScenarioTitle, s.ClientDisplayName, s.ClientInitials, s.Difficulty, s.State, s.EndReason,
        s.TranscriptStatus, s.ReportStatus, s.ReportErrorCode, s.IsSimulated, s.CreatedAtUtc, s.StartedAtUtc, s.EndedAtUtc,
        $"/ws/sessions/{s.Id}", $"/sessions/{s.Id}/report");
}

public sealed record TranscriptTurnDto(Guid TurnId, string Label, int Sequence, TurnSpeaker Speaker, string Text, TurnStatus Status, bool? FullyHeard);

public sealed record TranscriptResponse(Guid SessionId, int Revision, TranscriptStatus Status, string Notice, IReadOnlyList<TranscriptTurnDto> Turns)
{
    public static TranscriptResponse From(SessionSnapshot snapshot, IReadOnlyList<TranscriptTurn> turns) => new(
        snapshot.Id,
        snapshot.TranscriptRevision,
        snapshot.TranscriptStatus,
        CoachingPromptBuilder.TranscriptNotice,
        turns.Select(t => new TranscriptTurnDto(t.Id, $"T{t.Sequence}", t.Sequence, t.Speaker, t.Text, t.Status, t.FullyHeard)).ToList());
}

public sealed record ReportResponse(Guid SessionId, ReportStatus Status, string? ErrorCode, string Message, bool Retryable, CoachingReport? Report)
{
    public static ReportResponse From(SessionSnapshot snapshot, CoachingReport? report)
    {
        var (message, retryable) = snapshot.ReportStatus switch
        {
            ReportStatus.NotRequested when snapshot.TranscriptRevision == 0 => ("The report will be prepared after the conversation ends.", false),
            ReportStatus.NotRequested => ("No report has been requested.", true),
            ReportStatus.SetupRequired => ("Coaching setup is pending: an Azure report deployment is not configured. Your transcript was captured.", false),
            ReportStatus.Queued => ("Your coaching report is queued.", false),
            ReportStatus.Generating => ("Your coaching report is being generated.", false),
            ReportStatus.Ready => ("Your coaching report is ready.", false),
            ReportStatus.Failed => (FailureMessage(snapshot.ReportErrorCode), snapshot.ReportErrorCode is not ("report_refused" or "report_content_filtered" or "provider_not_configured")),
            _ => ("Unknown report status.", false),
        };
        return new ReportResponse(snapshot.Id, snapshot.ReportStatus, snapshot.ReportErrorCode, message, retryable, snapshot.ReportStatus == ReportStatus.Ready ? report : null);
    }

    private static string FailureMessage(string? code) => code switch
    {
        "report_refused" or "report_content_filtered" => "The coaching service declined to assess this transcript. Your transcript is still available below.",
        "report_auth_failed" => "The coaching service rejected the application's credentials. Please contact the administrator.",
        "report_throttled" or "report_unavailable" => "The coaching service is busy or unavailable. Please try again.",
        "invalid_report_output" => "The generated report did not pass evidence validation. Please try again.",
        _ => "The coaching report could not be generated. Please try again.",
    };
}

public sealed record CreateDemonstrationResponse(Guid DemonstrationId, string WebSocketPath, int MaxDurationSeconds, bool Simulated);

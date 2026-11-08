using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AdvisorCoachStudio.Web.Pages.Sessions;

public sealed class ReportModel(PracticeSessionStore store, AccessCodeService access, ScenarioCatalog catalog, ProviderReadiness readiness, ReportQueue queue) : PageModel
{
    private Dictionary<Guid, TranscriptTurn> _turnsById = [];

    public SessionSnapshot Snapshot { get; private set; } = null!;
    public IReadOnlyList<TranscriptTurn> Turns { get; private set; } = [];
    public CoachingReport? Report { get; private set; }
    public DemonstrationSnapshot? Demonstration { get; private set; }
    public bool DemonstrationAvailable { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public bool CanRetry { get; private set; }

    public IActionResult OnGet(Guid sessionId)
    {
        if (Load(sessionId) is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public IActionResult OnPostRetry(Guid sessionId)
    {
        var session = Load(sessionId);
        if (session is null)
        {
            return NotFound();
        }

        if (readiness.Reports().Status != ProviderReadinessStatus.SetupRequired && session.TryQueueReport(userRetry: true))
        {
            queue.Enqueue(session.Id);
        }

        return Redirect($"/sessions/{sessionId}/report");
    }

    public IActionResult OnPostDiscard(Guid sessionId)
    {
        if (access.TryGetOwner(User, out var owner))
        {
            store.Discard(sessionId, owner.OwnerId);
        }

        return Redirect("/");
    }

    private PracticeSession? Load(Guid sessionId)
    {
        if (!access.TryGetOwner(User, out var owner) || store.GetOwned(sessionId, owner.OwnerId) is not { } session)
        {
            return null;
        }

        Snapshot = session.Snapshot();
        Turns = session.Turns();
        _turnsById = Turns.ToDictionary(t => t.Id);
        Report = Snapshot.ReportStatus == ReportStatus.Ready ? session.Report : null;
        Demonstration = session.Demonstration?.Snapshot();
        DemonstrationAvailable = readiness.DemonstrationAvailable && Snapshot.State == ConversationState.Ended;
        var response = Contracts.ReportResponse.From(Snapshot, Report);
        StatusMessage = Snapshot.State is ConversationState.Ended ? response.Message : "Your conversation is still in progress or finalizing.";
        CanRetry = response.Retryable && Snapshot.ReportStatus is ReportStatus.Failed && readiness.Reports().Status != ProviderReadinessStatus.SetupRequired
            && session.ReportRequestCount < PracticeSession.MaxReportRequests;
        return session;
    }

    public bool IsPending => Snapshot.ReportStatus is ReportStatus.Queued or ReportStatus.Generating || SessionStateMachine.IsLive(Snapshot.State);

    public string ScenarioTitle(string id) => catalog.TryGet(id, out var s) ? s.Title : id;

    public static string CategoryName(string id) => Rubric.DisplayName(id);

    public string TurnLabel(TranscriptTurn turn) => $"T{turn.Sequence}";

    /// <summary>Renders the saved transcript excerpt for a validated quote (never an independently modified string).</summary>
    public (string Label, string Excerpt)? Excerpt(ReportEvidence evidence)
    {
        if (Report is null || !Report.TurnLabels.TryGetValue(evidence.TurnId, out var turnId) || !_turnsById.TryGetValue(turnId, out var turn))
        {
            return null;
        }

        var index = turn.Text.IndexOf(evidence.Quote, StringComparison.Ordinal);
        return index < 0 ? null : (evidence.TurnId, turn.Text.Substring(index, evidence.Quote.Length));
    }

    public static string SpeakerName(TurnSpeaker speaker, string clientName) => speaker == TurnSpeaker.Advisor ? "You (advisor)" : clientName;

    public static string StatusNote(TranscriptTurn turn) => turn.Status switch
    {
        TurnStatus.Interrupted => "Interrupted: not fully heard",
        TurnStatus.Failed => "Captions unavailable",
        TurnStatus.Pending => "Incomplete caption",
        _ => "",
    };

    public string EndReasonText => Snapshot.EndReason switch
    {
        SessionEndReason.UserEnded => "You ended the session.",
        SessionEndReason.DurationLimit => "The session reached its time limit.",
        SessionEndReason.Disconnected => "The connection was lost, so the session ended.",
        SessionEndReason.ProviderError => "The voice service had a problem, so the session ended early.",
        SessionEndReason.ConnectionQuality => "The session ended because of connection quality problems.",
        SessionEndReason.AccessExpired => "The session ended because access expired.",
        _ => "",
    };
}

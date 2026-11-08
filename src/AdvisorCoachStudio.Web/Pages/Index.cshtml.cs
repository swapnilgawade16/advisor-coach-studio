using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Pages;

public sealed class IndexModel(ScenarioCatalog catalog, ProviderReadiness readiness, PracticeSessionStore store, AccessCodeService access, IOptionsMonitor<RealtimeOptions> realtime) : PageModel
{
    public IReadOnlyList<PublicScenario> Scenarios { get; private set; } = [];
    public CapabilityState Realtime { get; private set; } = null!;
    public SessionSnapshot? Current { get; private set; }
    public int MaxMinutes => Math.Max(1, realtime.CurrentValue.MaxSessionMinutes);

    public string CurrentStatusText => Current switch
    {
        null => "",
        { State: ConversationState.Ended, ReportStatus: ReportStatus.Ready } => "report ready",
        { State: ConversationState.Ended } => "conversation ended",
        { State: ConversationState.Created } => "not started",
        { State: ConversationState.Failed } => "could not connect",
        { State: ConversationState.Expired } => "expired",
        _ => "in progress",
    };

    public void OnGet()
    {
        Scenarios = catalog.PublicCatalog();
        Realtime = readiness.Realtime();
        if (access.TryGetOwner(User, out var owner))
        {
            Current = store.GetCurrent(owner.OwnerId)?.Snapshot();
        }
    }
}

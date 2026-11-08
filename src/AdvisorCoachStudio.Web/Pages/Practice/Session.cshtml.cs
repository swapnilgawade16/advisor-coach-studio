using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Pages.Practice;

public sealed class SessionModel(PracticeSessionStore store, AccessCodeService access, IOptionsMonitor<RealtimeOptions> realtime) : PageModel
{
    public SessionSnapshot Snapshot { get; private set; } = null!;
    public LiveConversationModel Live { get; private set; } = null!;

    public IActionResult OnGet(Guid sessionId)
    {
        if (!access.TryGetOwner(User, out var owner) || store.GetOwned(sessionId, owner.OwnerId) is not { } session)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        Snapshot = session.Snapshot();
        Live = new LiveConversationModel(Snapshot.ScenarioTitle, Snapshot.ClientDisplayName, Snapshot.ClientInitials,
            DifficultyInfo.NameOf(Snapshot.Difficulty), Math.Max(1, realtime.CurrentValue.MaxSessionMinutes) * 60);
        return Page();
    }
}

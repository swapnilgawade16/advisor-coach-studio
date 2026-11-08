using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Pages.Practice;

public sealed class SetupModel(ScenarioCatalog catalog, ProviderReadiness readiness, IOptionsMonitor<RealtimeOptions> realtime) : PageModel
{
    public PublicScenario Scenario { get; private set; } = null!;
    public CapabilityState Realtime { get; private set; } = null!;
    public int MaxMinutes => Math.Max(1, realtime.CurrentValue.MaxSessionMinutes);
    public LiveConversationModel Live { get; private set; } = null!;

    public IActionResult OnGet(string scenarioId)
    {
        if (!catalog.TryGet(scenarioId, out var scenario))
        {
            return NotFound();
        }

        // Only the public projection reaches the page: no hidden facts, prompts, or triggers.
        Scenario = ScenarioCatalog.ToPublic(scenario);
        Realtime = readiness.Realtime();
        Live = new LiveConversationModel(Scenario.Title, Scenario.ClientDisplayName, Scenario.ClientInitials, "Intermediate", MaxMinutes * 60);
        return Page();
    }
}

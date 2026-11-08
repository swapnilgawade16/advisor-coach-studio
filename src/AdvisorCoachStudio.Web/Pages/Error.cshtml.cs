using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AdvisorCoachStudio.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string? CorrelationId { get; private set; }

    public void OnGet() => CorrelationId = Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier;
}

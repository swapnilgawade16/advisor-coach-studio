using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AdvisorCoachStudio.Web.Pages;

public sealed class AccessModel(AccessCodeService access, AccessAttemptLimiter limiter, PracticeSessionStore store) : PageModel
{
    [BindProperty]
    public string? Code { get; set; }

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (access.IsValid(User))
        {
            return Redirect("/");
        }

        if (!access.IsConfigured)
        {
            ErrorMessage = "Access is not available yet. Please contact the administrator.";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!limiter.TryAcquire(HttpContext))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ErrorMessage = "Too many attempts. Please wait a few minutes and try again.";
            return Page();
        }

        var result = await AccessEndpoints.SignInAsync(HttpContext, Code, access);
        Code = null;
        switch (result)
        {
            case AccessCodeResult.Valid:
                return Redirect("/");
            case AccessCodeResult.NotConfigured:
                ErrorMessage = "Access is not available yet. Please contact the administrator.";
                break;
            default:
                ErrorMessage = "That access code is not valid.";
                break;
        }

        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await AccessEndpoints.SignOutAsync(HttpContext, access, store);
        return Redirect("/access");
    }
}

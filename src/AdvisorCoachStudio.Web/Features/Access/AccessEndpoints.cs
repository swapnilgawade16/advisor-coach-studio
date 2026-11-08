using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AdvisorCoachStudio.Web.Features.Access;

public static class AccessEndpoints
{
    public const string RateLimitPolicy = "access-code";

    public static void MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/access").AddEndpointFilter<ValidateAntiforgeryFilter>().WithNoStore();

        group.MapPost("/validate", async (HttpContext http, AccessCodeRequest request, AccessCodeService access, AccessAttemptLimiter limiter) =>
        {
            if (!limiter.TryAcquire(http))
            {
                return ApiProblems.Create(http, StatusCodes.Status429TooManyRequests, "rate_limited", "Too many attempts. Please wait a few minutes and try again.", retryable: true, retryAfter: TimeSpan.FromMinutes(1));
            }

            var result = await SignInAsync(http, request.Code, access);
            return result switch
            {
                AccessCodeResult.Valid => Results.Ok(new { ok = true }),
                AccessCodeResult.NotConfigured => ApiProblems.Create(http, StatusCodes.Status503ServiceUnavailable, "access_not_configured", "Access is not available yet. Please contact the administrator."),
                _ => ApiProblems.Create(http, StatusCodes.Status400BadRequest, "invalid_access_code", "That access code is not valid."),
            };
        }).AllowAnonymous();

        group.MapPost("/logout", async (HttpContext http, AccessCodeService access, PracticeSessionStore store) =>
        {
            await SignOutAsync(http, access, store);
            return Results.NoContent();
        }).AllowAnonymous();
    }

    /// <summary>Shared by the JSON API and the access Razor page. Never logs the submitted code.</summary>
    public static async Task<AccessCodeResult> SignInAsync(HttpContext http, string? code, AccessCodeService access)
    {
        var result = access.Validate(code);
        if (result != AccessCodeResult.Valid)
        {
            return result;
        }

        var (principal, expires) = access.CreatePrincipal();
        await http.SignInAsync(AccessDefaults.Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = expires,
            AllowRefresh = false,
        });
        return result;
    }

    public static async Task SignOutAsync(HttpContext http, AccessCodeService access, PracticeSessionStore store)
    {
        if (access.TryGetOwner(http.User, out var owner))
        {
            store.DiscardOwner(owner.OwnerId);
            access.Revoke(owner);
        }

        await http.SignOutAsync(AccessDefaults.Scheme);
    }

    public static void ConfigureCookie(CookieAuthenticationOptions options, bool isDevelopment)
    {
        options.Cookie.Name = AccessDefaults.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.Cookie.IsEssential = true;
        options.SlidingExpiration = false;
        options.LoginPath = "/access";
        options.AccessDeniedPath = "/access";
        options.Events.OnValidatePrincipal = context =>
        {
            var access = context.HttpContext.RequestServices.GetRequiredService<AccessCodeService>();
            if (!access.IsValid(context.Principal))
            {
                context.RejectPrincipal();
                return context.HttpContext.SignOutAsync(AccessDefaults.Scheme);
            }

            return Task.CompletedTask;
        };
        options.Events.OnRedirectToLogin = context =>
        {
            if (IsApiOrSocket(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return ApiProblems.Unauthorized(context.HttpContext).ExecuteAsync(context.HttpContext);
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    private static bool IsApiOrSocket(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        request.Path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase);
}

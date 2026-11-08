using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;

namespace AdvisorCoachStudio.Web.Infrastructure.Http;

/// <summary>Stable, safe Problem Details responses. Never include provider bodies, secrets, or stack traces.</summary>
public static class ApiProblems
{
    public static string CorrelationId(HttpContext context) => Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    public static IResult Create(HttpContext context, int status, string code, string title, bool retryable = false, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } delay)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Results.Problem(
            statusCode: status,
            title: title,
            type: $"https://advisor-coach-studio.local/errors/{code}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["correlationId"] = CorrelationId(context),
                ["retryable"] = retryable,
            });
    }

    public static IResult NotFound(HttpContext context) =>
        Create(context, StatusCodes.Status404NotFound, "not_found", "The requested session was not found.");

    public static IResult Unauthorized(HttpContext context) =>
        Create(context, StatusCodes.Status401Unauthorized, "access_required", "Enter the shared access code to continue.");

    public static IResult Conflict(HttpContext context, string code, string title) =>
        Create(context, StatusCodes.Status409Conflict, code, title);

    public static IResult ProviderNotConfigured(HttpContext context, string title) =>
        Create(context, StatusCodes.Status503ServiceUnavailable, "provider_not_configured", title);
}

/// <summary>Validates the antiforgery header on state-changing JSON API requests.</summary>
public sealed class ValidateAntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return ApiProblems.Create(http, StatusCodes.Status400BadRequest, "csrf_validation_failed", "The request could not be verified. Reload the page and try again.", retryable: true);
            }
        }

        return await next(context);
    }
}

public static class NoStoreExtensions
{
    public static TBuilder WithNoStore<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });
}

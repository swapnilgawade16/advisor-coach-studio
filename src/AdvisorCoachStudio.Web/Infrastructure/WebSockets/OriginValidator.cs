using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Infrastructure.WebSockets;

/// <summary>
/// Exact Origin validation for WebSocket upgrades. CORS does not protect WebSockets, so a missing or unapproved
/// Origin is rejected before the upgrade and before any Azure connection is opened.
/// </summary>
public sealed class OriginValidator(IOptionsMonitor<SecurityOptions> options)
{
    public bool IsAllowed(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) || context.Request.Headers.Origin.Count != 1)
        {
            return false;
        }

        var configured = options.CurrentValue.AllowedOrigins;
        if (configured.Length > 0)
        {
            return configured.Any(allowed => string.Equals(allowed.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
        }

        // Default: same origin as the (possibly forwarded) request.
        var expected = $"{context.Request.Scheme}://{context.Request.Host.Value}";
        return string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase);
    }
}

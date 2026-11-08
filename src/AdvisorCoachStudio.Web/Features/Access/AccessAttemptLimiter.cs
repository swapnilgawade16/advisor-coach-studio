using System.Threading.RateLimiting;

namespace AdvisorCoachStudio.Web.Features.Access;

/// <summary>
/// Per-IP limit on access-code attempts, shared by the JSON API and the access page form. Partitioning by IP
/// means one misbehaving client cannot lock every user out.
/// </summary>
public sealed class AccessAttemptLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    public bool TryAcquire(HttpContext context)
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using var lease = _limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

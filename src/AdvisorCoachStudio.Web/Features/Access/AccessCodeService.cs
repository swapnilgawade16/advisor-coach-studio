using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Features.Access;

public static class AccessDefaults
{
    public const string Scheme = "AcsAccess";
    public const string CookieName = "acs_access";
    public const string OwnerClaim = "acs:owner";
    public const string CodeVersionClaim = "acs:cv";
    public const string ExpiresClaim = "acs:exp";
}

public enum AccessCodeResult
{
    Valid,
    Invalid,
    NotConfigured,
}

/// <summary>Anonymous owner identity carried in the protected access cookie.</summary>
public readonly record struct AccessOwner(Guid OwnerId, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Shared access-code validation and anonymous access lifecycle. Stateless apart from an in-memory
/// revocation list used for sign-out (entries expire with the cookie they revoke).
/// </summary>
public sealed class AccessCodeService(IOptionsMonitor<AccessGateOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _revoked = new();

    public bool IsConfigured => !string.IsNullOrEmpty(options.CurrentValue.SharedCode);

    public AccessCodeResult Validate(string? submitted)
    {
        var configured = options.CurrentValue.SharedCode;
        if (string.IsNullOrEmpty(configured))
        {
            return AccessCodeResult.NotConfigured;
        }

        // Hashing first gives fixed-length inputs so the comparison time does not leak the code length.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(submitted ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? AccessCodeResult.Valid : AccessCodeResult.Invalid;
    }

    public (ClaimsPrincipal Principal, DateTimeOffset ExpiresAtUtc) CreatePrincipal()
    {
        var expires = time.GetUtcNow().AddHours(Math.Clamp(options.CurrentValue.CookieHours, 1, 24 * 7));
        var identity = new ClaimsIdentity(
            [
                new Claim(AccessDefaults.OwnerClaim, Guid.NewGuid().ToString("N")),
                new Claim(AccessDefaults.CodeVersionClaim, options.CurrentValue.CodeVersion),
                new Claim(AccessDefaults.ExpiresClaim, expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            AccessDefaults.Scheme);
        return (new ClaimsPrincipal(identity), expires);
    }

    /// <summary>Checks expiry, code-version rotation, and sign-out revocation.</summary>
    public bool IsValid(ClaimsPrincipal? principal) => TryGetOwner(principal, out _);

    public bool TryGetOwner(ClaimsPrincipal? principal, out AccessOwner owner)
    {
        owner = default;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var ownerValue = principal.FindFirstValue(AccessDefaults.OwnerClaim);
        var version = principal.FindFirstValue(AccessDefaults.CodeVersionClaim);
        var expiresValue = principal.FindFirstValue(AccessDefaults.ExpiresClaim);
        if (!Guid.TryParseExact(ownerValue, "N", out var ownerId) ||
            !long.TryParse(expiresValue, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix) ||
            !string.Equals(version, options.CurrentValue.CodeVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds(expiresUnix);
        if (expires <= time.GetUtcNow() || _revoked.ContainsKey(ownerId))
        {
            return false;
        }

        owner = new AccessOwner(ownerId, expires);
        return true;
    }

    public void Revoke(AccessOwner owner) => _revoked[owner.OwnerId] = owner.ExpiresAtUtc;

    public int SweepRevocations()
    {
        var now = time.GetUtcNow();
        var removed = 0;
        foreach (var (ownerId, expires) in _revoked)
        {
            if (expires <= now && _revoked.TryRemove(ownerId, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}

using System.Globalization;
using System.Security.Claims;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;

namespace AdvisorCoachStudio.UnitTests.Access;

public sealed class AccessCodeServiceTests
{
    private const string SharedCode = "synthetic-shared-code";

    private readonly FakeTimeProvider _time = new();
    private readonly TestOptionsMonitor<AccessGateOptions> _options = new(new AccessGateOptions { SharedCode = SharedCode, CodeVersion = "1", CookieHours = 8 });

    private AccessCodeService CreateService() => new(_options, _time);

    [Fact]
    public void ValidateAcceptsTheConfiguredCode()
    {
        var service = CreateService();

        Assert.True(service.IsConfigured);
        Assert.Equal(AccessCodeResult.Valid, service.Validate(SharedCode));
    }

    [Theory]
    [InlineData("wrong-code")]
    [InlineData("synthetic-shared-cod")]
    [InlineData("synthetic-shared-code ")]
    [InlineData("SYNTHETIC-SHARED-CODE")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateRejectsAnyOtherCode(string? submitted) => Assert.Equal(AccessCodeResult.Invalid, CreateService().Validate(submitted));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateReportsNotConfiguredWithoutASharedCode(string? configured)
    {
        _options.Set(new AccessGateOptions { SharedCode = configured });
        var service = CreateService();

        Assert.False(service.IsConfigured);
        Assert.Equal(AccessCodeResult.NotConfigured, service.Validate(SharedCode));
        Assert.Equal(AccessCodeResult.NotConfigured, service.Validate(configured));
    }

    [Fact]
    public void CreatePrincipalRoundTripsThroughTryGetOwner()
    {
        var service = CreateService();

        var (principal, expires) = service.CreatePrincipal();

        Assert.Equal(_time.GetUtcNow().AddHours(8), expires);
        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal(AccessDefaults.Scheme, principal.Identity?.AuthenticationType);
        Assert.Equal("1", principal.FindFirstValue(AccessDefaults.CodeVersionClaim));
        Assert.True(service.TryGetOwner(principal, out var owner));
        Assert.NotEqual(Guid.Empty, owner.OwnerId);
        Assert.Equal(owner.OwnerId.ToString("N"), principal.FindFirstValue(AccessDefaults.OwnerClaim));
        Assert.Equal(expires, owner.ExpiresAtUtc);
        Assert.True(service.IsValid(principal));
    }

    [Fact]
    public void EachPrincipalHasADistinctAnonymousOwner()
    {
        var service = CreateService();

        Assert.True(service.TryGetOwner(service.CreatePrincipal().Principal, out var first));
        Assert.True(service.TryGetOwner(service.CreatePrincipal().Principal, out var second));

        Assert.NotEqual(first.OwnerId, second.OwnerId);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(24, 24)]
    [InlineData(10_000, 168)]
    public void CookieLifetimeIsClampedBetweenOneHourAndOneWeek(int configuredHours, int expectedHours)
    {
        _options.Set(new AccessGateOptions { SharedCode = SharedCode, CookieHours = configuredHours });

        var (_, expires) = CreateService().CreatePrincipal();

        Assert.Equal(_time.GetUtcNow().AddHours(expectedHours), expires);
    }

    [Fact]
    public void PrincipalExpiresWithItsCookie()
    {
        var service = CreateService();
        var (principal, _) = service.CreatePrincipal();

        _time.Advance(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1));
        Assert.True(service.TryGetOwner(principal, out _));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(service.TryGetOwner(principal, out var owner));
        Assert.Equal(default, owner);
        Assert.False(service.IsValid(principal));
    }

    [Fact]
    public void ChangingTheCodeVersionInvalidatesExistingPrincipals()
    {
        var service = CreateService();
        var (issuedBeforeRotation, _) = service.CreatePrincipal();

        _options.Set(new AccessGateOptions { SharedCode = SharedCode, CodeVersion = "2" });

        Assert.False(service.TryGetOwner(issuedBeforeRotation, out _));
        var (issuedAfterRotation, _) = service.CreatePrincipal();
        Assert.True(service.TryGetOwner(issuedAfterRotation, out _));
        Assert.Equal("2", issuedAfterRotation.FindFirstValue(AccessDefaults.CodeVersionClaim));
    }

    [Fact]
    public void RevokeInvalidatesOnlyThatOwner()
    {
        var service = CreateService();
        var (revoked, _) = service.CreatePrincipal();
        var (other, _) = service.CreatePrincipal();
        Assert.True(service.TryGetOwner(revoked, out var owner));

        service.Revoke(owner);

        Assert.False(service.TryGetOwner(revoked, out _));
        Assert.False(service.IsValid(revoked));
        Assert.True(service.TryGetOwner(other, out _));
    }

    [Fact]
    public void SweepRevocationsRemovesOnlyExpiredEntries()
    {
        var service = CreateService();
        var (early, _) = service.CreatePrincipal();
        _time.Advance(TimeSpan.FromHours(1));
        var (late, _) = service.CreatePrincipal();
        Assert.True(service.TryGetOwner(early, out var earlyOwner));
        Assert.True(service.TryGetOwner(late, out var lateOwner));
        service.Revoke(earlyOwner);
        service.Revoke(lateOwner);

        Assert.Equal(0, service.SweepRevocations());

        _time.SetUtcNow(earlyOwner.ExpiresAtUtc);
        Assert.Equal(1, service.SweepRevocations());
        Assert.False(service.TryGetOwner(late, out _));

        _time.SetUtcNow(lateOwner.ExpiresAtUtc);
        Assert.Equal(1, service.SweepRevocations());
        Assert.Equal(0, service.SweepRevocations());
    }

    [Fact]
    public void TryGetOwnerRejectsMissingOrMalformedClaims()
    {
        var service = CreateService();
        var expires = _time.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var ownerId = Guid.NewGuid().ToString("N");

        Assert.False(service.TryGetOwner(null, out _));
        Assert.False(service.TryGetOwner(new ClaimsPrincipal(new ClaimsIdentity(Claims(ownerId, "1", expires))), out _));
        Assert.False(service.TryGetOwner(Principal(Guid.NewGuid().ToString("D"), "1", expires), out _));
        Assert.False(service.TryGetOwner(Principal(ownerId, "1", "not-a-number"), out _));
        Assert.False(service.TryGetOwner(Principal(ownerId, "1", "-1"), out _));
        Assert.False(service.TryGetOwner(Principal(ownerId, null, expires), out _));
        Assert.True(service.TryGetOwner(Principal(ownerId, "1", expires), out var owner));
        Assert.Equal(Guid.ParseExact(ownerId, "N"), owner.OwnerId);
    }

    private static ClaimsPrincipal Principal(string owner, string? version, string expires) =>
        new(new ClaimsIdentity(Claims(owner, version, expires), AccessDefaults.Scheme));

    private static List<Claim> Claims(string owner, string? version, string expires)
    {
        var claims = new List<Claim> { new(AccessDefaults.OwnerClaim, owner), new(AccessDefaults.ExpiresClaim, expires) };
        if (version is not null)
        {
            claims.Add(new Claim(AccessDefaults.CodeVersionClaim, version));
        }

        return claims;
    }
}

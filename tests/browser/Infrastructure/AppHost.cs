using AdvisorCoachStudio.Web.Features.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// Runs the real application (WebApplicationFactory&lt;Program&gt;) on Kestrel at http://127.0.0.1:{free port}.
/// http://127.0.0.1 is a secure context, so getUserMedia and AudioWorklet work without TLS.
/// </summary>
public abstract class AppHost : IAsyncLifetime
{
    public const string AccessCode = "browser-test-code";

    private HostFactory? _factory;

    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Base URL without a trailing slash, for Playwright's BaseURL and cookie URLs.</summary>
    public string Origin => BaseAddress.GetLeftPart(UriPartial.Authority);

    public IServiceProvider Services => (_factory ?? throw new InvalidOperationException("The application host has not started.")).Services;

    public InMemoryLogSink Logs { get; } = new();

    /// <summary>Configuration for this host flavour, applied as host settings so Program.cs sees them before Build().</summary>
    protected abstract IEnumerable<KeyValuePair<string, string?>> Settings();

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    public ValueTask InitializeAsync()
    {
        var factory = new HostFactory(this);
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        factory.StartServer();
        _factory = factory;
        BaseAddress = factory.ClientOptions.BaseAddress;
        if (!string.Equals(BaseAddress.Host, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected the test server on http://127.0.0.1 but it listens on {BaseAddress}.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        Logs.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Issues a valid access cookie exactly as the access page would (same claims, scheme, and data protection),
    /// without spending one of the ten per-IP access attempts allowed every five minutes. The access page itself
    /// is exercised by <c>AccessTests</c>.
    /// </summary>
    public AccessGrant CreateAccessGrant()
    {
        var access = Services.GetRequiredService<AccessCodeService>();
        var (principal, expires) = access.CreatePrincipal();
        var cookieOptions = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AccessDefaults.Scheme);
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = expires,
            AllowRefresh = false,
        };
        var value = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(principal, properties, AccessDefaults.Scheme));
        var ownerClaim = principal.FindFirst(AccessDefaults.OwnerClaim)?.Value ?? throw new InvalidOperationException("The access principal has no owner claim.");
        var cookie = new Cookie
        {
            Name = cookieOptions.Cookie.Name ?? AccessDefaults.CookieName,
            Value = value,
            Url = Origin,
            HttpOnly = true,
            SameSite = SameSiteAttribute.Lax,
        };
        return new AccessGrant(cookie, new AccessOwner(Guid.ParseExact(ownerClaim, "N"), expires));
    }

    private static IEnumerable<KeyValuePair<string, string?>> CommonSettings() =>
    [
        new("AccessGate:SharedCode", AccessCode),
        new("AccessGate:CodeVersion", "browser-tests"),

        // Generous capacity so a relay that is still finalizing from a previous test never blocks the next one.
        new("Realtime:MaxConcurrentSessions", "20"),
    ];

    private sealed class HostFactory(AppHost owner) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            foreach (var (key, value) in CommonSettings().Concat(owner.Settings()))
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(owner.Logs);
            });
            builder.ConfigureTestServices(owner.ConfigureTestServices);
        }
    }
}

/// <summary>An access cookie for one anonymous owner, plus the owner identity it carries.</summary>
public sealed record AccessGrant(Cookie Cookie, AccessOwner Owner);

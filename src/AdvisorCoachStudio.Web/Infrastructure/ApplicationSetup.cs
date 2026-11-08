using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using AdvisorCoachStudio.Web.Infrastructure.Http;
using AdvisorCoachStudio.Web.Infrastructure.Simulation;
using AdvisorCoachStudio.Web.Infrastructure.WebSockets;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;

namespace AdvisorCoachStudio.Web.Infrastructure;

public static class ApplicationSetup
{
    public static IServiceCollection AddAdvisorCoachStudio(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.Section));
        services.AddOptions<AccessGateOptions>().Bind(configuration.GetSection(AccessGateOptions.Section));
        services.AddOptions<AzureRealtimeOptions>().Bind(configuration.GetSection(AzureRealtimeOptions.Section));
        services.AddOptions<AzureReportsOptions>().Bind(configuration.GetSection(AzureReportsOptions.Section));
        services.AddOptions<RealtimeOptions>().Bind(configuration.GetSection(RealtimeOptions.Section));
        services.AddOptions<DemonstrationOptions>().Bind(configuration.GetSection(DemonstrationOptions.Section));
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.Section));

        var mode = configuration.GetSection(AiOptions.Section).Get<AiOptions>()?.Mode ?? AiMode.Unconfigured;
        if (mode == AiMode.DevelopmentSimulation && !environment.IsDevelopment())
        {
            // Simulation must never run in a hosted/production deployment.
            throw new InvalidOperationException("Ai:Mode=DevelopmentSimulation is only allowed when ASPNETCORE_ENVIRONMENT=Development.");
        }

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => ScenarioCatalog.LoadFromDirectory(Path.Combine(environment.ContentRootPath, "Scenarios")));
        services.AddSingleton(sp => new ReportSchema(Path.Combine(environment.ContentRootPath, "Schemas", ReportSchema.FileName), sp.GetRequiredService<ScenarioCatalog>()));
        services.AddSingleton<AccessCodeService>();
        services.AddSingleton<AccessAttemptLimiter>();
        services.AddSingleton<ProviderHealthTracker>();
        services.AddSingleton<ProviderReadiness>();
        services.AddSingleton<PracticeSessionStore>();
        services.AddSingleton<LiveConnectionLimiter>();
        services.AddSingleton<ReportQueue>();
        services.AddSingleton<RealtimeRelayFactory>();
        services.AddSingleton<OriginValidator>();
        services.AddSingleton<CoachingReportGenerator>();
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton<AzureTokenProvider>();
        services.AddHttpClient(AzureChatReportClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

        if (mode == AiMode.DevelopmentSimulation)
        {
            services.AddSingleton<IRealtimeProviderConnector, SimulatedRealtimeConnector>();
            services.AddSingleton<IReportModelClient, SimulatedReportModelClient>();
        }
        else
        {
            services.AddSingleton<IRealtimeProviderConnector, AzureRealtimeConnector>();
            services.AddSingleton<IReportModelClient, AzureChatReportClient>();
        }

        services.AddSingleton<ReportWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ReportWorker>());
        services.AddHostedService<SessionSweepService>();

        services.AddDataProtection().SetApplicationName("AdvisorCoachStudio");
        services.AddAuthentication(AccessDefaults.Scheme).AddCookie(AccessDefaults.Scheme, options => AccessEndpoints.ConfigureCookie(options, environment.IsDevelopment()));
        services.AddOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(AccessDefaults.Scheme)
            .Configure<IOptions<AccessGateOptions>>((options, gate) => options.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(gate.Value.CookieHours, 1, 24 * 7)));
        services.AddAuthorization();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "acs_csrf";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/");
            options.Conventions.AllowAnonymousToPage("/Access");
            options.Conventions.AllowAnonymousToPage("/Error");
        });
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
                new ValueTask(ApiProblems.Create(context.HttpContext, 429, "rate_limited", "Too many requests. Please wait a minute and try again.", retryable: true, retryAfter: TimeSpan.FromMinutes(1)).ExecuteAsync(context.HttpContext));
            options.AddPolicy(SessionEndpoints.SessionCreatePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst(AccessDefaults.OwnerClaim)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
        });
        services.AddHealthChecks().AddCheck<ReadinessHealthCheck>("application", tags: ["ready"]);

        var security = configuration.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
        if (security.UseForwardedHeaders)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                options.ForwardLimit = 1;
                foreach (var proxy in security.KnownProxies)
                {
                    if (System.Net.IPAddress.TryParse(proxy, out var address))
                    {
                        options.KnownProxies.Add(address);
                    }
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            services.AddOpenTelemetry().UseAzureMonitor().WithMetrics(metrics => metrics.AddMeter(RelayMetrics.Meter.Name, "AdvisorCoachStudio.Reports"));
        }

        return services;
    }

    public static WebApplication UseAdvisorCoachStudio(this WebApplication app)
    {
        var security = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
        if (security.UseForwardedHeaders)
        {
            app.UseForwardedHeaders();
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.Use(SecurityHeaders);
        app.UseStaticFiles();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapRazorPages();
        app.MapAccessEndpoints();
        app.MapApplicationApi();
        app.MapRealtimeSockets();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

        LogStartupConfiguration(app);
        return app;
    }

    private static Task SecurityHeaders(HttpContext context, Func<Task> next)
    {
        var headers = context.Response.Headers;
        var host = context.Request.Host.Value;
        headers.ContentSecurityPolicy =
            $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self' blob:; " +
            $"connect-src 'self' wss://{host} ws://{host}; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "microphone=(self), camera=(), geolocation=(), payment=()";
        headers.XFrameOptions = "DENY";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        return next();
    }

    private static void LogStartupConfiguration(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AdvisorCoachStudio.Startup");
        var readiness = app.Services.GetRequiredService<ProviderReadiness>();
        var access = app.Services.GetRequiredService<AccessCodeService>();
        logger.LogInformation("AI mode: {Mode}. Realtime: {Realtime}. Reports: {Reports}. Access code configured: {AccessConfigured}.",
            readiness.Mode, readiness.Realtime().Status, readiness.Reports().Status, access.IsConfigured);
        if (readiness.MissingRealtimeSettings().Count > 0)
        {
            logger.LogWarning("Azure realtime settings missing or invalid: {Settings}.", string.Join(", ", readiness.MissingRealtimeSettings()));
        }

        if (readiness.MissingReportSettings().Count > 0)
        {
            logger.LogWarning("Azure report settings missing or invalid: {Settings}.", string.Join(", ", readiness.MissingReportSettings()));
        }

        if (readiness.IsSimulation)
        {
            logger.LogWarning("DevelopmentSimulation is active: {Label}.", ProviderReadiness.SimulationLabel);
        }
    }
}

/// <summary>Readiness without calling AI services or exposing configuration.</summary>
public sealed class ReadinessHealthCheck(ScenarioCatalog catalog) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(catalog.All.Count > 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
}

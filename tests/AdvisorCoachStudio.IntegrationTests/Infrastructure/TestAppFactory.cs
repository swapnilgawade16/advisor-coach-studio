using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

public sealed record TestAppOptions
{
    public bool RealtimeConfigured { get; init; } = true;

    public bool ReportsConfigured { get; init; } = true;

    /// <summary>Extra configuration overrides (for example Realtime:FinalizationDrainSeconds).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Hosts the real application (Testing environment, Ai:Mode=Azure) with only the provider boundaries replaced:
/// the realtime connector points at the local fake server and the report model client is scripted.
/// </summary>
public sealed class TestAppFactory(
    TestAppOptions options,
    FakeRealtimeServer fake,
    ScriptedReportModelClient reports,
    CapturingLoggerProvider logs,
    AdjustableTimeProvider clock) : WebApplicationFactory<Program>
{
    public const string AccessCode = "test-code";
    public const string FakeEndpoint = "https://fake.openai.azure.com";

    public FakeProviderConnector Connector { get; } = new(fake);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in BuildSettings())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRealtimeProviderConnector>();
            services.AddSingleton<IRealtimeProviderConnector>(Connector);
            services.RemoveAll<IReportModelClient>();
            services.AddSingleton<IReportModelClient>(reports);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<ILoggerProvider>();
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
    }

    private Dictionary<string, string> BuildSettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ai:Mode"] = "Azure",
            ["AccessGate:SharedCode"] = AccessCode,
            ["Realtime:FinalizationDrainSeconds"] = "2",
            ["AzureRealtime:Endpoint"] = "",
            ["AzureReports:Endpoint"] = "",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "",
        };

        if (options.RealtimeConfigured)
        {
            settings["AzureRealtime:Endpoint"] = FakeEndpoint;
            settings["AzureRealtime:Deployment"] = "rt";
            settings["AzureRealtime:Authentication"] = "ApiKey";
            settings["AzureRealtime:ApiKey"] = "unused";
            settings["AzureRealtime:TranscriptionModel"] = "whisper-1";
            settings["AzureRealtime:Voice"] = "alloy";
        }

        if (options.ReportsConfigured)
        {
            settings["AzureReports:Endpoint"] = FakeEndpoint;
            settings["AzureReports:Deployment"] = "reports";
            settings["AzureReports:Authentication"] = "ApiKey";
            settings["AzureReports:ApiKey"] = "unused";
        }

        foreach (var (key, value) in options.Settings)
        {
            settings[key] = value;
        }

        return settings;
    }
}

/// <summary>One isolated application instance per test: the app, its fake provider, scripted reports, logs, and clock.</summary>
public sealed class TestApp : IAsyncDisposable
{
    private TestApp(TestAppOptions options, FakeRealtimeServer fake)
    {
        Fake = fake;
        Factory = new TestAppFactory(options, fake, Reports, Logs, Clock);
    }

    public FakeRealtimeServer Fake { get; }

    public ScriptedReportModelClient Reports { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public AdjustableTimeProvider Clock { get; } = new();

    public TestAppFactory Factory { get; }

    public IServiceProvider Services => Factory.Services;

    public static async Task<TestApp> StartAsync(TestAppOptions? options = null)
    {
        var fake = await FakeRealtimeServer.StartAsync();
        var app = new TestApp(options ?? new TestAppOptions(), fake);
        try
        {
            app.Factory.StartServer();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public T Service<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public TestUser CreateUser() => new(this);

    public async Task<TestUser> LoginAsync()
    {
        var user = CreateUser();
        await user.LoginAsync();
        return user;
    }

    /// <summary>Waits until every live realtime slot (roleplay or demonstration) has been released.</summary>
    public Task WaitForLiveConnectionsReleasedAsync() =>
        Wait.UntilAsync(() => Service<LiveConnectionLimiter>().ActiveCount == 0, "all live realtime connection slots to be released");

    public string Diagnostics() => Logs.Describe();

    public async ValueTask DisposeAsync()
    {
        Reports.Shutdown();
        await Factory.DisposeAsync();
        await Fake.DisposeAsync();
    }
}

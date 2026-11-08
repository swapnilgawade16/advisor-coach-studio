using AdvisorCoachStudio.Web.Domain;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Infrastructure.Configuration;

public sealed record CapabilityState(ProviderReadinessStatus Status, string Code, string Message, bool Simulated)
{
    public bool IsReady => Status == ProviderReadinessStatus.Ready;
}

/// <summary>
/// Computes safe provider readiness from configuration only. Never calls Azure and never exposes configuration values.
/// </summary>
public sealed class ProviderReadiness(
    IOptionsMonitor<AiOptions> ai,
    IOptionsMonitor<AzureRealtimeOptions> realtime,
    IOptionsMonitor<AzureReportsOptions> reports,
    IOptionsMonitor<DemonstrationOptions> demonstration,
    ProviderHealthTracker health,
    IHostEnvironment environment)
{
    public const string SimulationLabel = "Simulated development data - not Azure";

    public AiMode Mode => ai.CurrentValue.Mode;

    public bool IsSimulation => Mode == AiMode.DevelopmentSimulation && environment.IsDevelopment();

    public CapabilityState Realtime()
    {
        switch (Mode)
        {
            case AiMode.DevelopmentSimulation when environment.IsDevelopment():
                return new(ProviderReadinessStatus.Ready, "ready", SimulationLabel, true);
            case AiMode.Azure:
                if (realtime.CurrentValue.Validate().Count > 0)
                {
                    return SetupRequired("Live voice practice needs Azure realtime configuration.");
                }

                return health.IsRealtimeDegraded
                    ? new(ProviderReadinessStatus.Unavailable, "service_unavailable", "The voice service is temporarily unavailable. Please try again shortly.", false)
                    : new(ProviderReadinessStatus.Ready, "ready", "Ready", false);
            default:
                return SetupRequired("Azure setup required. Live voice practice is not configured yet.");
        }
    }

    public CapabilityState Reports()
    {
        switch (Mode)
        {
            case AiMode.DevelopmentSimulation when environment.IsDevelopment():
                return new(ProviderReadinessStatus.Ready, "ready", SimulationLabel, true);
            case AiMode.Azure:
                if (reports.CurrentValue.Validate().Count > 0)
                {
                    return SetupRequired("Coaching reports need an Azure report deployment. Your transcript is still captured.");
                }

                return health.IsReportsDegraded
                    ? new(ProviderReadinessStatus.Unavailable, "service_unavailable", "The coaching service is temporarily unavailable.", false)
                    : new(ProviderReadinessStatus.Ready, "ready", "Ready", false);
            default:
                return SetupRequired("Azure setup required for coaching reports.");
        }
    }

    public bool DemonstrationAvailable => demonstration.CurrentValue.Enabled && Realtime().IsReady;

    /// <summary>Names of missing settings, for operator logs only (never returned to browsers).</summary>
    public IReadOnlyList<string> MissingRealtimeSettings() => Mode == AiMode.Azure ? realtime.CurrentValue.Validate() : [];

    public IReadOnlyList<string> MissingReportSettings() => Mode == AiMode.Azure ? reports.CurrentValue.Validate() : [];

    private static CapabilityState SetupRequired(string message) =>
        new(ProviderReadinessStatus.SetupRequired, "provider_not_configured", message, false);
}

/// <summary>Tracks recent provider failures so readiness can report "temporarily unavailable" without probing Azure.</summary>
public sealed class ProviderHealthTracker(TimeProvider time)
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private int _realtimeFailures;
    private DateTimeOffset _realtimeDegradedUntil;
    private int _reportFailures;
    private DateTimeOffset _reportsDegradedUntil;

    public bool IsRealtimeDegraded
    {
        get { lock (_gate) { return time.GetUtcNow() < _realtimeDegradedUntil; } }
    }

    public bool IsReportsDegraded
    {
        get { lock (_gate) { return time.GetUtcNow() < _reportsDegradedUntil; } }
    }

    public void RealtimeSucceeded()
    {
        lock (_gate) { _realtimeFailures = 0; _realtimeDegradedUntil = default; }
    }

    public void RealtimeFailed(bool authenticationFailure)
    {
        lock (_gate)
        {
            _realtimeFailures++;
            if (authenticationFailure || _realtimeFailures >= 2)
            {
                _realtimeDegradedUntil = time.GetUtcNow() + Cooldown;
            }
        }
    }

    public void ReportsSucceeded()
    {
        lock (_gate) { _reportFailures = 0; _reportsDegradedUntil = default; }
    }

    public void ReportsFailed(bool authenticationFailure)
    {
        lock (_gate)
        {
            _reportFailures++;
            if (authenticationFailure || _reportFailures >= 3)
            {
                _reportsDegradedUntil = time.GetUtcNow() + Cooldown;
            }
        }
    }
}

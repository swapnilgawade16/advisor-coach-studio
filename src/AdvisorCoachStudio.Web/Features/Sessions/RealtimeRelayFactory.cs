using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using System.Security.Claims;
using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Features.Sessions;

public enum RelayMode
{
    Roleplay,
    Demonstration,
}

/// <summary>Creates relays with their shared dependencies.</summary>
public sealed class RealtimeRelayFactory(
    IRealtimeProviderConnector connector,
    ProviderReadiness readiness,
    ProviderHealthTracker health,
    AccessCodeService access,
    ReportQueue reports,
    IOptionsMonitor<RealtimeOptions> realtimeOptions,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILoggerFactory loggers)
{
    public RealtimeRelay CreateRoleplay(PracticeSession session, WebSocket browser, ClaimsPrincipal user, AccessOwner owner, string correlationId) =>
        new(RelayMode.Roleplay, session, null, browser, user, owner, correlationId, this);

    public RealtimeRelay CreateDemonstration(PracticeSession session, DemonstrationSession demonstration, WebSocket browser, ClaimsPrincipal user, AccessOwner owner, string correlationId, string instructions) =>
        new(RelayMode.Demonstration, session, demonstration, browser, user, owner, correlationId, this) { DemonstrationInstructions = instructions };

    internal IRealtimeProviderConnector Connector => connector;
    internal ProviderReadiness Readiness => readiness;
    internal ProviderHealthTracker Health => health;
    internal AccessCodeService Access => access;
    internal ReportQueue Reports => reports;
    internal RealtimeOptions Options => realtimeOptions.CurrentValue;
    internal CancellationToken ApplicationStopping => lifetime.ApplicationStopping;
    internal TimeProvider Time => time;
    internal ILogger Logger { get; } = loggers.CreateLogger<RealtimeRelay>();
}

/// <summary>
/// Token bucket limiting microphone audio to real time plus a bounded burst (network batching).
/// Accounting is in bytes (48 bytes per millisecond at 24 kHz PCM16) so small frames cannot round down to zero.
/// </summary>
public sealed class AudioRateBudget(int burstMs, TimeProvider time)
{
    private readonly double _capacityBytes = (double)burstMs * AppProtocol.BytesPerMillisecond;
    private double _availableBytes = (double)burstMs * AppProtocol.BytesPerMillisecond;
    private long _lastTimestamp = time.GetTimestamp();

    public bool TryConsume(int audioBytes)
    {
        var now = time.GetTimestamp();
        var refill = time.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds * AppProtocol.BytesPerMillisecond;
        _availableBytes = Math.Min(_capacityBytes, _availableBytes + refill);
        _lastTimestamp = now;
        if (audioBytes > _availableBytes)
        {
            return false;
        }

        _availableBytes -= audioBytes;
        return true;
    }
}

/// <summary>Metadata-only telemetry. Never records transcript text or audio.</summary>
public static class RelayMetrics
{
    public static readonly Meter Meter = new("AdvisorCoachStudio.Realtime");
    public static readonly Counter<long> Connections = Meter.CreateCounter<long>("acs.realtime.connections");
    public static readonly Counter<long> ConnectionFailures = Meter.CreateCounter<long>("acs.realtime.connection_failures");
    public static readonly Counter<long> Interruptions = Meter.CreateCounter<long>("acs.realtime.interruptions");
    public static readonly Counter<long> ProviderErrors = Meter.CreateCounter<long>("acs.realtime.provider_errors");
    public static readonly Counter<long> TranscriptsIncomplete = Meter.CreateCounter<long>("acs.realtime.transcripts_incomplete");
    public static readonly Histogram<double> SessionSeconds = Meter.CreateHistogram<double>("acs.realtime.session_seconds");
}

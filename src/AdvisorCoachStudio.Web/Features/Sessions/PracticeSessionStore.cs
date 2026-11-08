using System.Collections.Concurrent;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Features.Sessions;

/// <summary>
/// In-memory store holding at most one current practice session per anonymous owner.
/// Creating a new session discards the previous one (including any live connection and report work).
/// </summary>
public sealed class PracticeSessionStore(TimeProvider time, IOptionsMonitor<RealtimeOptions> realtime, ILogger<PracticeSessionStore> logger)
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Guid, PracticeSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Guid> _currentByOwner = new();

    public int Count => _sessions.Count;

    public PracticeSession Create(AccessOwner owner, ScenarioDefinition scenario, Difficulty difficulty, string promptVersion, bool simulated)
    {
        lock (_gate)
        {
            if (_currentByOwner.TryGetValue(owner.OwnerId, out var previousId))
            {
                DiscardCore(previousId, "replaced");
            }

            var session = new PracticeSession(owner.OwnerId, owner.ExpiresAtUtc, scenario, difficulty, promptVersion, simulated, time.GetUtcNow().UtcDateTime);
            _sessions[session.Id] = session;
            _currentByOwner[owner.OwnerId] = session.Id;
            return session;
        }
    }

    /// <summary>Returns the session only when it exists, is not discarded, and belongs to the owner.</summary>
    public PracticeSession? GetOwned(Guid sessionId, Guid ownerId) =>
        _sessions.TryGetValue(sessionId, out var session) && session.OwnerId == ownerId && !session.IsDiscarded ? session : null;

    public PracticeSession? Get(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) && !session.IsDiscarded ? session : null;

    public PracticeSession? GetCurrent(Guid ownerId) =>
        _currentByOwner.TryGetValue(ownerId, out var id) ? GetOwned(id, ownerId) : null;

    public (PracticeSession Session, DemonstrationSession Demonstration)? FindDemonstration(Guid demonstrationId, Guid ownerId)
    {
        var session = GetCurrent(ownerId);
        var demonstration = session?.Demonstration;
        return session is not null && demonstration is not null && demonstration.Id == demonstrationId ? (session, demonstration) : null;
    }

    public bool Discard(Guid sessionId, Guid ownerId)
    {
        lock (_gate)
        {
            if (GetOwned(sessionId, ownerId) is null)
            {
                return false;
            }

            DiscardCore(sessionId, "discarded");
            return true;
        }
    }

    public void DiscardOwner(Guid ownerId)
    {
        lock (_gate)
        {
            if (_currentByOwner.TryGetValue(ownerId, out var id))
            {
                DiscardCore(id, "signed_out");
            }
        }
    }

    /// <summary>Expires never-started sessions and discards sessions whose access cookie has expired.</summary>
    public int Sweep()
    {
        var now = time.GetUtcNow();
        var unstartedCutoff = now.UtcDateTime.AddMinutes(-Math.Max(1, realtime.CurrentValue.UnstartedSessionMinutes));
        var removed = 0;
        foreach (var session in _sessions.Values)
        {
            if (session.OwnerExpiresAtUtc <= now)
            {
                lock (_gate)
                {
                    DiscardCore(session.Id, "access_expired");
                }

                removed++;
            }
            else if (session.State == ConversationState.Created && session.CreatedAtUtc < unstartedCutoff)
            {
                session.TryTransition(ConversationState.Expired, now.UtcDateTime);
            }
        }

        return removed;
    }

    private void DiscardCore(Guid sessionId, string reason)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.MarkDiscarded();
            _currentByOwner.TryRemove(new KeyValuePair<Guid, Guid>(session.OwnerId, sessionId));
            logger.LogInformation("Practice session {SessionId} discarded ({Reason}).", sessionId, reason);
        }
    }
}

public enum LiveSlotResult
{
    Acquired,
    OwnerBusy,
    AtCapacity,
}

/// <summary>Process-wide live realtime connection cap (roleplay and demonstrations), and one live stream per owner.</summary>
public sealed class LiveConnectionLimiter(IOptionsMonitor<RealtimeOptions> options)
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _owners = [];
    private int _active;

    public int ActiveCount { get { lock (_gate) { return _active; } } }

    public LiveSlotResult Check(Guid ownerId)
    {
        lock (_gate)
        {
            return _owners.Contains(ownerId) ? LiveSlotResult.OwnerBusy
                : _active >= Math.Max(1, options.CurrentValue.MaxConcurrentSessions) ? LiveSlotResult.AtCapacity
                : LiveSlotResult.Acquired;
        }
    }

    public LiveSlotResult TryAcquire(Guid ownerId, out LiveConnectionLease? lease)
    {
        lock (_gate)
        {
            lease = null;
            var check = _owners.Contains(ownerId) ? LiveSlotResult.OwnerBusy
                : _active >= Math.Max(1, options.CurrentValue.MaxConcurrentSessions) ? LiveSlotResult.AtCapacity
                : LiveSlotResult.Acquired;
            if (check != LiveSlotResult.Acquired)
            {
                return check;
            }

            _active++;
            _owners.Add(ownerId);
            lease = new LiveConnectionLease(this, ownerId);
            return LiveSlotResult.Acquired;
        }
    }

    /// <summary>Waits briefly for a previous connection of the same owner to finish releasing (e.g. after a reset).</summary>
    public async Task<(LiveSlotResult Result, LiveConnectionLease? Lease)> AcquireAsync(Guid ownerId, TimeSpan ownerWait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ownerWait;
        while (true)
        {
            var result = TryAcquire(ownerId, out var lease);
            if (result != LiveSlotResult.OwnerBusy || DateTime.UtcNow >= deadline)
            {
                return (result, lease);
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    internal void Release(Guid ownerId)
    {
        lock (_gate)
        {
            if (_owners.Remove(ownerId))
            {
                _active--;
            }
        }
    }
}

public sealed class LiveConnectionLease(LiveConnectionLimiter limiter, Guid ownerId) : IDisposable
{
    private int _released;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            limiter.Release(ownerId);
        }
    }
}

/// <summary>Periodic in-memory housekeeping: expire unstarted sessions, drop expired owners, prune revocations.</summary>
public sealed class SessionSweepService(PracticeSessionStore store, AccessCodeService access, ILogger<SessionSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                store.Sweep();
                access.SweepRevocations();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "In-memory session sweep failed.");
            }
        }
    }
}

using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using AdvisorCoachStudio.Web.Prompts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdvisorCoachStudio.UnitTests.Sessions;

public sealed class PracticeSessionStoreTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly TestOptionsMonitor<RealtimeOptions> _realtime = new(new RealtimeOptions { UnstartedSessionMinutes = 10 });
    private readonly PracticeSessionStore _store;

    public PracticeSessionStoreTests() => _store = new PracticeSessionStore(_time, _realtime, NullLogger<PracticeSessionStore>.Instance);

    [Fact]
    public void CreateForTheSameOwnerDiscardsThePreviousSession()
    {
        var owner = Owner(TimeSpan.FromHours(8));
        var previous = Create(owner);

        var current = Create(owner);

        Assert.Null(_store.GetOwned(previous.Id, owner.OwnerId));
        Assert.Null(_store.Get(previous.Id));
        Assert.True(previous.IsDiscarded);
        Assert.True(previous.Lifetime.IsCancellationRequested);
        Assert.Same(current, _store.GetCurrent(owner.OwnerId));
        Assert.False(current.Lifetime.IsCancellationRequested);
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public void SessionsAreOnlyVisibleToTheirOwner()
    {
        var owner = Owner(TimeSpan.FromHours(8));
        var stranger = Owner(TimeSpan.FromHours(8));
        var session = Create(owner);

        Assert.Same(session, _store.GetOwned(session.Id, owner.OwnerId));
        Assert.Null(_store.GetOwned(session.Id, stranger.OwnerId));
        Assert.Null(_store.GetCurrent(stranger.OwnerId));
        Assert.False(_store.Discard(session.Id, stranger.OwnerId));
        Assert.False(session.IsDiscarded);
    }

    [Fact]
    public void DifferentOwnersKeepIndependentSessions()
    {
        var first = Create(Owner(TimeSpan.FromHours(8)));
        var second = Create(Owner(TimeSpan.FromHours(8)));

        Assert.Equal(2, _store.Count);
        Assert.False(first.IsDiscarded);
        Assert.False(second.IsDiscarded);
    }

    [Fact]
    public void DiscardRemovesTheSessionAndCancelsItsWork()
    {
        var owner = Owner(TimeSpan.FromHours(8));
        var session = Create(owner);

        Assert.True(_store.Discard(session.Id, owner.OwnerId));

        Assert.Null(_store.GetOwned(session.Id, owner.OwnerId));
        Assert.Null(_store.GetCurrent(owner.OwnerId));
        Assert.True(session.Lifetime.IsCancellationRequested);
        Assert.False(_store.Discard(session.Id, owner.OwnerId));
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void DiscardOwnerDiscardsOnlyThatOwnersSession()
    {
        var signedOut = Owner(TimeSpan.FromHours(8));
        var other = Owner(TimeSpan.FromHours(8));
        var discarded = Create(signedOut);
        var kept = Create(other);

        _store.DiscardOwner(signedOut.OwnerId);
        _store.DiscardOwner(Guid.NewGuid());

        Assert.True(discarded.IsDiscarded);
        Assert.Null(_store.GetCurrent(signedOut.OwnerId));
        Assert.Same(kept, _store.GetCurrent(other.OwnerId));
        Assert.False(kept.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public void SweepDiscardsSessionsWhoseAccessCookieExpired()
    {
        var shortLived = Owner(TimeSpan.FromHours(1));
        var longLived = Owner(TimeSpan.FromHours(8));
        var expiring = Create(shortLived);
        var surviving = Create(longLived);
        Assert.True(expiring.TryTransition(ConversationState.Connecting, _time.GetUtcNow().UtcDateTime));

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, _store.Sweep());

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, _store.Sweep());

        Assert.True(expiring.IsDiscarded);
        Assert.True(expiring.Lifetime.IsCancellationRequested);
        Assert.Null(_store.GetOwned(expiring.Id, shortLived.OwnerId));
        Assert.Same(surviving, _store.GetCurrent(longLived.OwnerId));
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public void SweepExpiresNeverStartedSessionsAfterTheConfiguredMinutes()
    {
        var owner = Owner(TimeSpan.FromHours(8));
        var unstarted = Create(owner);
        var startedOwner = Owner(TimeSpan.FromHours(8));
        var started = Create(startedOwner);
        Assert.True(started.TryTransition(ConversationState.Connecting, _time.GetUtcNow().UtcDateTime));

        _time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        _store.Sweep();
        Assert.Equal(ConversationState.Created, unstarted.State);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, _store.Sweep());

        Assert.Equal(ConversationState.Expired, unstarted.State);
        Assert.NotNull(unstarted.Snapshot().EndedAtUtc);
        Assert.Same(unstarted, _store.GetOwned(unstarted.Id, owner.OwnerId));
        Assert.Equal(ConversationState.Connecting, started.State);
    }

    [Fact]
    public void SweepUsesTheCurrentUnstartedSessionSetting()
    {
        _realtime.Set(new RealtimeOptions { UnstartedSessionMinutes = 3 });
        var session = Create(Owner(TimeSpan.FromHours(8)));

        _time.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        _store.Sweep();

        Assert.Equal(ConversationState.Expired, session.State);
    }

    private AccessOwner Owner(TimeSpan lifetime) => new(Guid.NewGuid(), _time.GetUtcNow().Add(lifetime));

    private PracticeSession Create(AccessOwner owner) =>
        _store.Create(owner, TestScenarios.Shipped(TestScenarios.MarketDecline), Difficulty.Intermediate, RoleplayPromptBuilder.Version, simulated: false);
}

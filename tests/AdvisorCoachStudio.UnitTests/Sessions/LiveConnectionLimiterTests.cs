using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;

namespace AdvisorCoachStudio.UnitTests.Sessions;

public sealed class LiveConnectionLimiterTests
{
    private readonly TestOptionsMonitor<RealtimeOptions> _options = new(new RealtimeOptions { MaxConcurrentSessions = 2 });

    [Fact]
    public void EnforcesTheGlobalConnectionCap()
    {
        var limiter = new LiveConnectionLimiter(_options);

        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(Guid.NewGuid(), out var first));
        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(Guid.NewGuid(), out var second));
        var third = Guid.NewGuid();
        Assert.Equal(LiveSlotResult.AtCapacity, limiter.Check(third));
        Assert.Equal(LiveSlotResult.AtCapacity, limiter.TryAcquire(third, out var rejected));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(rejected);
        Assert.Equal(2, limiter.ActiveCount);
    }

    [Fact]
    public void AllowsOnlyOneLiveConnectionPerOwner()
    {
        var limiter = new LiveConnectionLimiter(_options);
        var owner = Guid.NewGuid();

        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(owner, out var lease));
        Assert.Equal(LiveSlotResult.OwnerBusy, limiter.Check(owner));
        Assert.Equal(LiveSlotResult.OwnerBusy, limiter.TryAcquire(owner, out var duplicate));

        Assert.NotNull(lease);
        Assert.Null(duplicate);
        Assert.Equal(1, limiter.ActiveCount);
    }

    [Fact]
    public void DisposingALeaseReleasesItsSlotExactlyOnce()
    {
        var limiter = new LiveConnectionLimiter(_options);
        var owner = Guid.NewGuid();
        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(owner, out var staleLease));
        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(Guid.NewGuid(), out _));

        staleLease!.Dispose();
        Assert.Equal(1, limiter.ActiveCount);
        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(owner, out var currentLease));

        staleLease.Dispose();

        Assert.Equal(2, limiter.ActiveCount);
        Assert.Equal(LiveSlotResult.OwnerBusy, limiter.Check(owner));
        currentLease!.Dispose();
        Assert.Equal(1, limiter.ActiveCount);
        Assert.Equal(LiveSlotResult.Acquired, limiter.Check(owner));
    }

    [Fact]
    public void CapacityFreedByOneOwnerCanBeUsedByAnother()
    {
        var limiter = new LiveConnectionLimiter(_options);
        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(Guid.NewGuid(), out var first));
        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(Guid.NewGuid(), out _));
        var waiting = Guid.NewGuid();
        Assert.Equal(LiveSlotResult.AtCapacity, limiter.TryAcquire(waiting, out _));

        first!.Dispose();

        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(waiting, out var lease));
        Assert.NotNull(lease);
    }

    [Fact]
    public void ANonPositiveCapStillAllowsOneConnection()
    {
        var limiter = new LiveConnectionLimiter(new TestOptionsMonitor<RealtimeOptions>(new RealtimeOptions { MaxConcurrentSessions = 0 }));

        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(Guid.NewGuid(), out _));
        Assert.Equal(LiveSlotResult.AtCapacity, limiter.TryAcquire(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task AcquireAsyncWaitsForTheSameOwnersPreviousLeaseToBeReleased()
    {
        var limiter = new LiveConnectionLimiter(_options);
        var owner = Guid.NewGuid();
        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(owner, out var previous));

        var acquiring = limiter.AcquireAsync(owner, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        Assert.False(acquiring.IsCompleted);
        previous!.Dispose();
        var (result, lease) = await acquiring;

        Assert.Equal(LiveSlotResult.Acquired, result);
        Assert.NotNull(lease);
        Assert.Equal(1, limiter.ActiveCount);
    }

    [Fact]
    public async Task AcquireAsyncGivesUpWhenTheOwnerStaysBusy()
    {
        var limiter = new LiveConnectionLimiter(_options);
        var owner = Guid.NewGuid();
        Assert.Equal(LiveSlotResult.Acquired, limiter.TryAcquire(owner, out _));

        var (result, lease) = await limiter.AcquireAsync(owner, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.Equal(LiveSlotResult.OwnerBusy, result);
        Assert.Null(lease);
    }
}

using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Features.Sessions;

namespace AdvisorCoachStudio.UnitTests.Sessions;

/// <summary>Microphone input is limited to real time plus a bounded burst (spec section 8.4), accounted in bytes.</summary>
public sealed class AudioRateBudgetTests
{
    private const int BytesPerMs = AppProtocol.BytesPerMillisecond;

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void TheBurstAllowanceIsAvailableImmediately()
    {
        var budget = new AudioRateBudget(2000, _time);

        Assert.True(budget.TryConsume(2000 * BytesPerMs));
        Assert.False(budget.TryConsume(2));
    }

    [Fact]
    public void AllowanceRefillsAtRealTime()
    {
        var budget = new AudioRateBudget(1000, _time);
        Assert.True(budget.TryConsume(1000 * BytesPerMs));

        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(budget.TryConsume(100 * BytesPerMs));
        Assert.False(budget.TryConsume(2));
    }

    [Fact]
    public void TwentyMillisecondFramesAreAcceptedOnlyAsFastAsRealTime()
    {
        var budget = new AudioRateBudget(1000, _time);
        Assert.True(budget.TryConsume(1000 * BytesPerMs));
        const int Frame = 960;

        _time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.False(budget.TryConsume(Frame));
        _time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(budget.TryConsume(Frame));
        Assert.False(budget.TryConsume(Frame));
    }

    [Fact]
    public void TinyFramesStillConsumeBudget()
    {
        var budget = new AudioRateBudget(1, _time);

        for (var frame = 0; frame < BytesPerMs / 2; frame++)
        {
            Assert.True(budget.TryConsume(2), $"Frame {frame} fits within one millisecond of audio.");
        }

        Assert.False(budget.TryConsume(2));
    }

    [Fact]
    public void IdleTimeNeverRaisesTheAllowanceAboveTheBurst()
    {
        var budget = new AudioRateBudget(500, _time);

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.False(budget.TryConsume((500 * BytesPerMs) + 2));
        Assert.True(budget.TryConsume(500 * BytesPerMs));
    }

    [Fact]
    public void RejectedAttemptsDoNotConsumeTheAllowance()
    {
        var budget = new AudioRateBudget(100, _time);

        Assert.False(budget.TryConsume((100 * BytesPerMs) + 2));
        Assert.True(budget.TryConsume(100 * BytesPerMs));
    }
}

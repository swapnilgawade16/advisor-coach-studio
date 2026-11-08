namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>
/// Manually advanced clock. Starts on a whole second so values survive Unix-second round trips. Timestamps follow
/// the same clock (one tick per 100 ns), so <see cref="TimeProvider.GetElapsedTime(long, long)"/> is deterministic.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private DateTimeOffset _now;

    public FakeTimeProvider()
        : this(DefaultStart)
    {
    }

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now = _now.Add(delta);
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate)
        {
            _now = value;
        }
    }
}

using Microsoft.Extensions.Logging;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception)
{
    public override string ToString() => $"[{Level}] {Category}: {Message}{(Exception is null ? "" : " | " + Exception.GetType().Name + ": " + Exception.Message)}";
}

/// <summary>
/// In-memory log sink for the application under test. Replaces console/event-log providers so tests stay quiet, and lets
/// tests await specific operational events. Set ACS_TEST_LOGS=1 to also echo entries to the console while debugging.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly History<LogEntry> _entries = new();
    private readonly bool _echo = Environment.GetEnvironmentVariable("ACS_TEST_LOGS") == "1";

    public IReadOnlyList<LogEntry> Entries => _entries.Snapshot();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public async Task<LogEntry> WaitForAsync(Func<LogEntry, bool> predicate, string description, TimeSpan? timeout = null) =>
        (await _entries.WaitForMatchesAsync(predicate, 1, timeout, () => $"Expected a log entry: {description}. Recent log:{Environment.NewLine}{Describe()}"))[0];

    public string Describe(int last = 40) => string.Join(Environment.NewLine, Entries.TakeLast(last));

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        _entries.Add(entry);
        if (_echo)
        {
            Console.WriteLine(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}

/// <summary>Wall-clock time that a test can move forward (for the server-enforced duration cap). Timers stay real.</summary>
public sealed class AdjustableTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan amount) => Interlocked.Add(ref _offsetTicks, amount.Ticks);
}

using Microsoft.Extensions.Logging;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// Keeps recent server log lines in memory so a failing test can print what the application did, instead of
/// flooding the console for passing tests.
/// </summary>
public sealed class InMemoryLogSink : ILoggerProvider
{
    private const int MaxLines = 5000;
    private readonly Lock _gate = new();
    private readonly Queue<(long Sequence, string Line)> _lines = new();
    private long _sequence;

    /// <summary>Sequence number of the next line; use it as a bookmark before a test runs.</summary>
    public long Position
    {
        get { lock (_gate) { return _sequence; } }
    }

    public IReadOnlyList<string> Since(long position)
    {
        lock (_gate)
        {
            return _lines.Where(entry => entry.Sequence >= position).Select(entry => entry.Line).ToList();
        }
    }

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue((_sequence++, line));
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }
    }

    private sealed class SinkLogger(InMemoryLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel,-11} {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            sink.Add(line);
        }
    }
}

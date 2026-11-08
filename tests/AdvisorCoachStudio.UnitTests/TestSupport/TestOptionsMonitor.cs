using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>Minimal <see cref="IOptionsMonitor{TOptions}"/> double whose current value can be swapped by a test.</summary>
internal sealed class TestOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
{
    public TOptions CurrentValue { get; private set; } = value;

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;

    public void Set(TOptions value) => CurrentValue = value;
}

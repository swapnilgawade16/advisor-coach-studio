using System.Text.Json;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>Bounded waiting helpers. Tests await specific conditions instead of sleeping for fixed periods.</summary>
public static class Wait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task UntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Default);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }

            await Task.Delay(20);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Default);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }

            await Task.Delay(25);
        }
    }

    public static async Task ForAsync(Task task, string description, TimeSpan? timeout = null)
    {
        try
        {
            await task.WaitAsync(timeout ?? Default);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out waiting for {description}.");
        }
    }

    public static async Task<T> ForAsync<T>(Task<T> task, string description, TimeSpan? timeout = null)
    {
        try
        {
            return await task.WaitAsync(timeout ?? Default);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out waiting for {description}.");
        }
    }
}

/// <summary>Thread-safe, append-only history that tests can wait on without consuming entries.</summary>
public sealed class History<T>
    where T : class
{
    private readonly Lock _gate = new();
    private readonly List<T> _items = [];
    private TaskCompletionSource _changed = Wait.NewSignal();
    private bool _completed;

    public int Count
    {
        get { lock (_gate) { return _items.Count; } }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }

    public void Add(T item)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _items.Add(item);
            signal = _changed;
            _changed = Wait.NewSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>Marks the source as finished: waits that cannot be satisfied any more fail fast.</summary>
    public void Complete()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _completed = true;
            signal = _changed;
            _changed = Wait.NewSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>Waits until at least <paramref name="count"/> entries match and returns the first <paramref name="count"/> matches.</summary>
    public async Task<IReadOnlyList<T>> WaitForMatchesAsync(Func<T, bool> predicate, int count, TimeSpan? timeout, Func<string> describeFailure)
    {
        using var cts = new CancellationTokenSource(timeout ?? Wait.Default);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var matches = _items.Where(predicate).Take(count).ToList();
                if (matches.Count >= count)
                {
                    return matches;
                }

                if (_completed)
                {
                    throw new InvalidOperationException(describeFailure() + " (the source has already ended)");
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(describeFailure());
            }
        }
    }
}

/// <summary>A JSON text message observed on a socket (browser application message or provider client event).</summary>
public sealed record JsonMessage(int Index, string Type, JsonElement Json, string Raw)
{
    public static JsonMessage Parse(int index, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement.Clone();
            var type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!
                : "(no type)";
            return new JsonMessage(index, type, root, raw);
        }
        catch (JsonException)
        {
            return new JsonMessage(index, "(invalid json)", default, raw);
        }
    }

    public JsonElement Property(string name) =>
        Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty(name, out var value)
            ? value
            : throw new KeyNotFoundException($"Message '{Type}' (#{Index}) has no '{name}' property: {Raw}");

    public bool Has(string name) => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty(name, out _);

    public string? String(string name) =>
        Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public int Int(string name) => Property(name).GetInt32();

    public long Long(string name) => Property(name).GetInt64();

    public bool? Bool(string name) =>
        Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public override string ToString() => $"#{Index} {Type}";
}

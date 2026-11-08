using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>Reads what the injected instrumentation (instrumentation.js) observed in a page.</summary>
public static class Probe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Script { get; } = Load();

    public static async Task<ProbeSnapshot> SnapshotAsync(this IPage page)
    {
        var element = await page.EvaluateAsync<JsonElement>("() => window.__acs.snapshot()");
        return element.Deserialize<ProbeSnapshot>(Json) ?? throw new InvalidOperationException("The page instrumentation is missing.");
    }

    public static Task<int> AudioAppendsAsync(this IPage page) => page.EvaluateAsync<int>("() => window.__acs.audioAppends");

    public static Task<int> GetUserMediaCallsAsync(this IPage page) => page.EvaluateAsync<int>("() => window.__acs.getUserMediaCalls");

    /// <summary>Waits (in the page, without sleeping) until more audio.append messages than <paramref name="count"/> were sent.</summary>
    public static Task WaitForAudioAppendsAboveAsync(this IPage page, int count, float timeoutMs = 15_000) =>
        page.WaitForFunctionAsync("count => window.__acs.audioAppends > count", count, new PageWaitForFunctionOptions { Timeout = timeoutMs });

    /// <summary>The record persisted by the instrumentation when the previous document in this tab was unloaded.</summary>
    public static async Task<UnloadRecord?> PreviousUnloadAsync(this IPage page)
    {
        var json = await page.EvaluateAsync<string?>("() => sessionStorage.getItem('__acs_unload')");
        return json is null ? null : JsonSerializer.Deserialize<UnloadRecord>(json, Json);
    }

    private static string Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("instrumentation.js")
            ?? throw new InvalidOperationException("The instrumentation.js resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed record ProbeSnapshot(
    int GetUserMediaCalls,
    IReadOnlyList<string> GetUserMediaErrors,
    IReadOnlyList<string> TrackStates,
    IReadOnlyList<int> SocketStates,
    IReadOnlyList<string> SocketUrls,
    IReadOnlyList<string> Sent,
    int AudioAppends,
    IReadOnlyList<string> Received,
    int AudioDeltas,
    IReadOnlyList<SocketClose> Closes,
    IReadOnlyList<WorkletRecord> Worklets,
    IReadOnlyList<string> States,
    IReadOnlyList<string?> TranscriptAdded,
    IReadOnlyList<TranscriptUpdate> TranscriptUpdates)
{
    public IReadOnlyList<string> WorkletMessages(string processorName) =>
        Worklets.Where(w => w.Name == processorName).SelectMany(w => w.Messages).ToList();

    /// <summary>Compact summary for assertion messages.</summary>
    public override string ToString() =>
        $"received=[{string.Join(", ", Received)}] closes=[{string.Join(", ", Closes)}] sent=[{string.Join(", ", Sent.Where(t => t != "playback.progress"))}] states=[{string.Join(", ", States)}]";
}

public sealed record SocketClose(int Code, string Reason, bool WasClean);

public sealed record WorkletRecord(string Name, IReadOnlyList<string> Messages);

public sealed record TranscriptUpdate(string? TurnId, string ClassName, string Text);

public sealed record UnloadRecord(IReadOnlyList<string> TrackStates, IReadOnlyList<int> SocketStates, IReadOnlyList<string> Sent);

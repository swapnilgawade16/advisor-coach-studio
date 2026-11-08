using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>Default behaviour applied to each accepted provider socket (copied at accept time, then adjustable per connection).</summary>
public sealed class FakeRealtimeBehavior
{
    public bool SendSessionCreated { get; set; } = true;

    /// <summary>Reply to session.update with session.updated.</summary>
    public bool AcknowledgeSessionUpdate { get; set; } = true;

    /// <summary>When set, session.update is answered with this error event instead of session.updated.</summary>
    public (string Type, string Code)? SessionUpdateError { get; set; }

    /// <summary>
    /// Instructions reported in session.updated, given the instructions the relay sent. Defaults to echoing them, as
    /// Azure does; return other text to simulate a provider that applied different instructions, or null to omit them.
    /// </summary>
    public Func<string?, string?> AppliedInstructions { get; set; } = sent => sent;

    /// <summary>Reply to conversation.item.truncate with conversation.item.truncated.</summary>
    public bool AcknowledgeTruncate { get; set; } = true;

    /// <summary>When set, the WebSocket upgrade itself is rejected with this HTTP status.</summary>
    public int? RejectUpgradeStatus { get; set; }

    /// <summary>
    /// Script streamed for the n-th (0-based) response.create on a connection; null means "do not answer automatically".
    /// Defaults to the scenario opening for roleplay and a scripted example for demonstrations.
    /// </summary>
    public Func<FakeRealtimeConnection, int, ScriptedResponse?> ResponseScript { get; set; } =
        (connection, number) => number > 0 ? null : connection.IsDemonstration ? Scripts.Demonstration : Scripts.Opening;

    internal FakeRealtimeBehavior Clone() => (FakeRealtimeBehavior)MemberwiseClone();
}

/// <summary>
/// Local fake of the Azure GA realtime endpoint: a real Kestrel server on a loopback port exposing
/// ws://127.0.0.1:{port}/openai/v1/realtime. Each accepted socket becomes a <see cref="FakeRealtimeConnection"/>.
/// </summary>
public sealed class FakeRealtimeServer : IAsyncDisposable
{
    public const string RealtimePath = "/openai/v1/realtime";

    private readonly Lock _gate = new();
    private readonly List<FakeRealtimeConnection> _connections = [];
    private readonly History<FakeRealtimeConnection> _accepted = new();
    private readonly CancellationTokenSource _stopping = new();
    private WebApplication? _app;
    private int _upgradeAttempts;

    private FakeRealtimeServer()
    {
    }

    public FakeRealtimeBehavior Behavior { get; } = new();

    public Uri RealtimeUri { get; private set; } = null!;

    /// <summary>Number of HTTP requests that reached the provider endpoint (accepted or not).</summary>
    public int UpgradeAttempts => Volatile.Read(ref _upgradeAttempts);

    public IReadOnlyList<FakeRealtimeConnection> Connections
    {
        get { lock (_gate) { return [.. _connections]; } }
    }

    public static async Task<FakeRealtimeServer> StartAsync()
    {
        var server = new FakeRealtimeServer();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "FakeRealtimeProvider",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map(RealtimePath, (RequestDelegate)server.HandleAsync);
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        server.RealtimeUri = new Uri($"ws://127.0.0.1:{port}{RealtimePath}?model=test");
        server._app = app;
        return server;
    }

    public async Task<FakeRealtimeConnection> WaitForConnectionAsync(int index, TimeSpan? timeout = null)
    {
        var matches = await _accepted.WaitForMatchesAsync(c => c.Index == index, 1, timeout,
            () => $"Fake provider did not accept connection #{index}; accepted {_accepted.Count}, upgrade attempts {UpgradeAttempts}.");
        return matches[0];
    }

    private async Task HandleAsync(HttpContext http)
    {
        Interlocked.Increment(ref _upgradeAttempts);
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (Behavior.RejectUpgradeStatus is { } status)
        {
            http.Response.StatusCode = status;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        FakeRealtimeConnection connection;
        lock (_gate)
        {
            connection = new FakeRealtimeConnection(_connections.Count, socket, http, Behavior.Clone());
            _connections.Add(connection);
        }

        _accepted.Add(connection);
        await connection.RunAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        foreach (var connection in Connections)
        {
            connection.Abort();
        }

        if (_app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _app.StopAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Remaining upgraded connections were aborted above; stopping is best effort.
            }

            await _app.DisposeAsync();
        }

        _stopping.Dispose();
    }
}

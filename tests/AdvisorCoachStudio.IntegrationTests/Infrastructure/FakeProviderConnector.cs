using System.Net;
using System.Net.WebSockets;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>
/// Provider-boundary replacement for <see cref="AzureRealtimeConnector"/>: a real <see cref="ClientWebSocket"/> to the
/// local fake server. Everything above the socket (handshake, relay, event parser, transcript assembly) is the product code.
/// </summary>
public sealed class FakeProviderConnector(FakeRealtimeServer server) : IRealtimeProviderConnector
{
    private int _connectCalls;

    public AzureRealtimeSessionSettings SessionSettings => new("whisper-1", "alloy");

    public int ConnectCalls => Volatile.Read(ref _connectCalls);

    public async Task<WebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectCalls);
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(server.RealtimeUri, cancellationToken);
            return socket;
        }
        catch (WebSocketException ex)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            var failure = status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderConnectFailure.Authentication,
                HttpStatusCode.TooManyRequests => ProviderConnectFailure.Throttled,
                HttpStatusCode.NotFound => ProviderConnectFailure.NotFound,
                _ => ProviderConnectFailure.Network,
            };
            throw new ProviderConnectException(failure, "Could not connect to the fake realtime provider.", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

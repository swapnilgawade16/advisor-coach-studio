using System.Net;
using System.Net.WebSockets;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

public enum ProviderConnectFailure
{
    NotConfigured,
    Authentication,
    Throttled,
    NotFound,
    Timeout,
    Network,
}

public sealed class ProviderConnectException(ProviderConnectFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public ProviderConnectFailure Failure { get; } = failure;
    public bool IsRetryable => Failure is ProviderConnectFailure.Timeout or ProviderConnectFailure.Network or ProviderConnectFailure.Throttled;
}

/// <summary>Opens the provider-side WebSocket. Implementations: Azure (real), development simulation, and test fakes.</summary>
public interface IRealtimeProviderConnector
{
    Task<WebSocket> ConnectAsync(CancellationToken cancellationToken);

    AzureRealtimeSessionSettings SessionSettings { get; }
}

/// <summary>Supplies bearer tokens for Entra ID authentication. Tokens are never logged or sent to browsers.</summary>
public sealed class AzureTokenProvider(TokenCredential credential)
{
    public async Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        return token.Token;
    }
}

/// <summary>
/// Connects to the Azure GA realtime WebSocket endpoint: wss://{resource}/openai/v1/realtime?model={deployment}.
/// No api-version and no OpenAI-Beta header. Credentials go in headers only, never in the URL.
/// </summary>
public sealed class AzureRealtimeConnector(IOptionsMonitor<AzureRealtimeOptions> options, AzureTokenProvider tokens, ILogger<AzureRealtimeConnector> logger)
    : IRealtimeProviderConnector
{
    public AzureRealtimeSessionSettings SessionSettings =>
        new(options.CurrentValue.TranscriptionModel ?? "", options.CurrentValue.Voice);

    public static Uri BuildUri(string endpoint, string deployment)
    {
        var root = new Uri(endpoint, UriKind.Absolute);
        if (root.Scheme != Uri.UriSchemeHttps)
        {
            throw new ProviderConnectException(ProviderConnectFailure.NotConfigured, "The realtime endpoint must use https.");
        }

        var builder = new UriBuilder(root)
        {
            Scheme = "wss",
            Port = root.IsDefaultPort ? -1 : root.Port,
            Path = "/openai/v1/realtime",
            Query = "model=" + Uri.EscapeDataString(deployment),
        };
        return builder.Uri;
    }

    public async Task<WebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (current.Validate().Count > 0)
        {
            throw new ProviderConnectException(ProviderConnectFailure.NotConfigured, "Azure realtime configuration is incomplete.");
        }

        var uri = BuildUri(current.Endpoint!, current.Deployment!);
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        if (current.Authentication == AzureAuthenticationMode.ApiKey)
        {
            socket.Options.SetRequestHeader("api-key", current.ApiKey);
        }
        else
        {
            string token;
            try
            {
                token = await tokens.GetTokenAsync(current.TokenScope, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                logger.LogWarning("Entra token acquisition for realtime failed: {Error}.", ex.GetType().Name);
                throw new ProviderConnectException(ProviderConnectFailure.Authentication, "Could not obtain a token for the realtime service.", ex);
            }

            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(current.ConnectTimeoutSeconds, 3, 60)));
        try
        {
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new ProviderConnectException(ProviderConnectFailure.Timeout, "Timed out connecting to the realtime service.");
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
            logger.LogWarning("Realtime connection failed: {Failure} (HTTP {Status}).", failure, (int)status);
            throw new ProviderConnectException(failure, "Could not connect to the realtime service.", ex);
        }
    }
}

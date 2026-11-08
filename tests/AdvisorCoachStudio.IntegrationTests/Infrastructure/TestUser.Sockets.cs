using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>A refused WebSocket upgrade: HTTP status plus the Problem Details code (when a body was written).</summary>
public sealed record HandshakeRejection(int Status, string? Code, string Body);

public sealed partial class TestUser
{
    /// <summary>Opens a browser-like application socket (access cookie, Origin, and the acs.v1 subprotocol by default).</summary>
    public async Task<AppSocket> ConnectAsync(string path, string? origin = BaseAddress, string? subprotocol = Subprotocol)
    {
        var client = CreateWebSocketClient(origin, subprotocol, captureBody: null);
        using var timeout = new CancellationTokenSource(Wait.Default);
        var socket = await client.ConnectAsync(new Uri("wss://localhost" + path), timeout.Token);
        return new AppSocket(socket, _app.Diagnostics);
    }

    /// <summary>Attempts an upgrade that the server must refuse, returning the status and problem code it wrote.</summary>
    public async Task<HandshakeRejection> ConnectExpectingRejectionAsync(string path, string? origin = BaseAddress, string? subprotocol = Subprotocol)
    {
        using var body = new MemoryStream();
        var client = CreateWebSocketClient(origin, subprotocol, body);
        using var timeout = new CancellationTokenSource(Wait.Default);
        try
        {
            var socket = await client.ConnectAsync(new Uri("wss://localhost" + path), timeout.Token);
            socket.Abort();
            socket.Dispose();
        }
        catch (InvalidOperationException ex) when (HandshakeStatus().Match(ex.Message) is { Success: true } match)
        {
            var status = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var text = Encoding.UTF8.GetString(body.ToArray());
            return new HandshakeRejection(status, ReadProblemCode(text), text);
        }

        throw new InvalidOperationException($"Expected the WebSocket upgrade for {path} to be rejected, but it was accepted.");
    }

    private WebSocketClient CreateWebSocketClient(string? origin, string? subprotocol, Stream? captureBody)
    {
        var client = _app.Factory.Server.CreateWebSocketClient();
        var cookies = CookieHeader;
        client.ConfigureRequest = request =>
        {
            if (cookies.Length > 0)
            {
                request.Headers.Cookie = cookies;
            }

            if (origin is not null)
            {
                request.Headers.Origin = origin;
            }

            if (captureBody is not null)
            {
                // Rejections are written as Problem Details; capture them (an accepted upgrade never writes a body).
                request.HttpContext.Response.Body = captureBody;
            }
        };

        if (subprotocol is not null)
        {
            client.SubProtocols.Add(subprotocol);
        }

        return client;
    }

    private static string? ReadProblemCode(string body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("status code: (\\d+)")]
    private static partial Regex HandshakeStatus();
}

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>
/// Browser side of the acs.v1 application protocol. A background loop assembles fragmented frames into messages and
/// records them; tests wait for specific messages (optionally after a given index) without consuming history.
/// </summary>
public sealed class AppSocket : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Func<string> _diagnostics;
    private readonly History<JsonMessage> _messages = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _closed = Wait.NewSignal();
    private readonly Task _receiveLoop;
    private long _clientSequence;

    internal AppSocket(WebSocket socket, Func<string> diagnostics)
    {
        _socket = socket;
        _diagnostics = diagnostics;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public string? SubProtocol => _socket.SubProtocol;

    public IReadOnlyList<JsonMessage> Messages => _messages.Snapshot();

    /// <summary>Index of the newest received message (-1 when none); use as an "after" marker for waits.</summary>
    public int LastIndex => _messages.Count - 1;

    public Task Closed => _closed.Task;

    public WebSocketCloseStatus? CloseStatus { get; private set; }

    public long ClientSequence => Interlocked.Read(ref _clientSequence);

    public async Task<JsonMessage> WaitForAsync(string type, Func<JsonMessage, bool>? predicate = null, int afterIndex = -1, TimeSpan? timeout = null) =>
        (await WaitForCountAsync(type, 1, predicate, afterIndex, timeout))[0];

    public Task<IReadOnlyList<JsonMessage>> WaitForCountAsync(string type, int count, Func<JsonMessage, bool>? predicate = null, int afterIndex = -1, TimeSpan? timeout = null) =>
        _messages.WaitForMatchesAsync(
            m => m.Index > afterIndex && m.Type == type && (predicate?.Invoke(m) ?? true),
            count,
            timeout,
            () => $"Browser socket did not receive {count} '{type}' message(s) after #{afterIndex}. Received: {Describe()}{Environment.NewLine}Server log:{Environment.NewLine}{_diagnostics()}");

    public Task<JsonMessage> WaitForStateAsync(string state, int afterIndex = -1, TimeSpan? timeout = null) =>
        WaitForAsync("session.state", m => m.String("state") == state, afterIndex, timeout);

    public Task WaitForClosedAsync(TimeSpan? timeout = null) => Wait.ForAsync(Closed, "the server to close the browser socket. Received: " + Describe(), timeout);

    public string Describe() => string.Join(", ", Messages.Select(m => m.Type switch
    {
        "session.state" => $"#{m.Index} state={m.String("state")}",
        "error" => $"#{m.Index} error={m.String("code")}",
        "session.ended" => $"#{m.Index} ended={m.String("reason")}/{m.String("transcriptStatus")}/{m.String("reportStatus")}",
        _ => $"#{m.Index} {m.Type}",
    }));

    // ---- Browser to server ---------------------------------------------------------------

    /// <summary>Serializes like a browser's JSON.stringify (no escaping of '+' or '/' inside base64 audio).</summary>
    public static readonly JsonSerializerOptions BrowserJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Task SendAsync(object message) => SendTextAsync(JsonSerializer.Serialize(message, BrowserJson));

    public Task SendTextAsync(string text) => SendFrameAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

    public Task SendBinaryAsync(byte[] data) => SendFrameAsync(data, WebSocketMessageType.Binary);

    /// <summary>Sends one audio.append with the next clientSequence and returns the base64 payload that was sent.</summary>
    public async Task<string> SendAudioAsync(byte[] pcm)
    {
        var audio = Convert.ToBase64String(pcm);
        var sequence = Interlocked.Increment(ref _clientSequence);
        await SendAsync(new { v = 1, type = "audio.append", clientSequence = sequence, audio });
        return audio;
    }

    public Task SendPlaybackProgressAsync(string responseId, string itemId, int playedMilliseconds) =>
        SendAsync(new { v = 1, type = "playback.progress", responseId, itemId, contentIndex = 0, playedMilliseconds });

    public Task SendPlaybackStoppedAsync(string responseId, string itemId, int playedMilliseconds) =>
        SendAsync(new { v = 1, type = "playback.stopped", responseId, itemId, contentIndex = 0, playedMilliseconds });

    public Task SendEndAsync(string reason = "user") => SendAsync(new { v = 1, type = "session.end", reason });

    public Task SendHeartbeatAsync() => SendAsync(new { v = 1, type = "heartbeat" });

    private async Task SendFrameAsync(byte[] payload, WebSocketMessageType type)
    {
        using var timeout = new CancellationTokenSource(Wait.Default);
        await _sendLock.WaitAsync(timeout.Token);
        try
        {
            await _socket.SendAsync(payload.AsMemory(), type, endOfMessage: true, timeout.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ---- Receive loop ----------------------------------------------------------------------

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CloseStatus = _socket.CloseStatus;
                    await CloseOutputAsync();
                    return;
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    var text = result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.WrittenSpan) : "{\"type\":\"(binary)\"}";
                    message.ResetWrittenCount();
                    _messages.Add(JsonMessage.Parse(_messages.Count, text));
                }
            }
        }
        catch (Exception)
        {
            // Aborted or cancelled: treated as closed.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _messages.Complete();
            _closed.TrySetResult();
        }
    }

    private async Task CloseOutputAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _sendLock.WaitAsync(timeout.Token);
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closing", timeout.Token);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Browser-initiated close (for example navigating away).</summary>
    public Task CloseAsync() => CloseOutputAsync();

    public async ValueTask DisposeAsync()
    {
        if (!Closed.IsCompleted)
        {
            try
            {
                await CloseOutputAsync();
                await Closed.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // Best effort: the test is finishing.
            }
        }

        await _stop.CancelAsync();
        try
        {
            await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // The loop only ends by close, abort, or cancellation.
        }

        _socket.Dispose();
        _stop.Dispose();
        _sendLock.Dispose();
    }
}

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>
/// One provider-side socket accepted by <see cref="FakeRealtimeServer"/>. Records every client event the relay sends,
/// applies the default GA behaviour (session.created, session.updated, scripted responses), and lets tests emit
/// arbitrary server events. All sends are serialized through one lock, as on a real socket.
/// </summary>
public sealed partial class FakeRealtimeConnection
{
    private readonly WebSocket _socket;
    private readonly HttpContext _http;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Lock _gate = new();
    private readonly History<JsonMessage> _received = new();
    private readonly TaskCompletionSource _closed = Wait.NewSignal();
    private readonly CancellationTokenSource _lifetime = new();
    private string? _lastItemId;
    private int _responseCreates;
    private int _eventIds;

    internal FakeRealtimeConnection(int index, WebSocket socket, HttpContext http, FakeRealtimeBehavior behavior)
    {
        Index = index;
        _socket = socket;
        _http = http;
        Behavior = behavior;
        Model = http.Request.Query["model"].ToString();
    }

    public int Index { get; }

    public FakeRealtimeBehavior Behavior { get; }

    public string Model { get; }

    public Task Closed => _closed.Task;

    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>Exception that ended the receive loop (null after a clean close handshake).</summary>
    public Exception? Failure { get; private set; }

    public int ResponseCreateCount => Volatile.Read(ref _responseCreates);

    /// <summary>Every client event received from the relay, in order.</summary>
    public IReadOnlyList<JsonMessage> Received => _received.Snapshot();

    /// <summary>Last conversation item announced by this fake; used as previous_item_id for the next item.</summary>
    public string? LastItemId
    {
        get { lock (_gate) { return _lastItemId; } }
    }

    /// <summary>True when the relay configured an output-only session (turn_detection null).</summary>
    public bool IsDemonstration
    {
        get
        {
            var update = Received.FirstOrDefault(e => e.Type == "session.update");
            return update is not null &&
                update.Json.TryGetProperty("session", out var session) &&
                session.TryGetProperty("audio", out var audio) &&
                audio.TryGetProperty("input", out var input) &&
                input.TryGetProperty("turn_detection", out var detection) &&
                detection.ValueKind == JsonValueKind.Null;
        }
    }

    public IReadOnlyList<JsonMessage> ReceivedOfType(string type) => [.. Received.Where(e => e.Type == type)];

    public async Task<JsonMessage> WaitForAsync(string type, Func<JsonMessage, bool>? predicate = null, TimeSpan? timeout = null) =>
        (await WaitForCountAsync(type, 1, predicate, timeout))[0];

    public Task<IReadOnlyList<JsonMessage>> WaitForCountAsync(string type, int count, Func<JsonMessage, bool>? predicate = null, TimeSpan? timeout = null) =>
        _received.WaitForMatchesAsync(e => e.Type == type && (predicate?.Invoke(e) ?? true), count, timeout,
            () => $"Fake provider connection #{Index} did not receive {count} '{type}' event(s). Received: {Describe()}");

    public Task WaitForClosedAsync(TimeSpan? timeout = null) => Wait.ForAsync(Closed, $"fake provider connection #{Index} to close", timeout);

    public string Describe() => string.Join(", ", Received.Select(e => e.Type));

    internal async Task RunAsync(CancellationToken serverStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStopping, _lifetime.Token, _http.RequestAborted);
        try
        {
            if (Behavior.SendSessionCreated)
            {
                await SendAsync(new { type = "session.created", event_id = NextEventId(), session = new { id = $"sess_fake_{Index}", type = "realtime", model = Model } });
            }

            while (await ReceiveTextAsync(linked.Token) is { } text)
            {
                var message = JsonMessage.Parse(_received.Count, text);
                _received.Add(message);
                await HandleAsync(message);
            }

            if (_socket.State == WebSocketState.CloseReceived)
            {
                await CloseOutputAsync();
            }
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
        finally
        {
            _received.Complete();
            _closed.TrySetResult();
            await _lifetime.CancelAsync();
        }
    }

    private async Task HandleAsync(JsonMessage message)
    {
        switch (message.Type)
        {
            case "session.update":
                if (Behavior.SessionUpdateError is { } error)
                {
                    await SendErrorAsync(error.Type, error.Code);
                }
                else if (Behavior.AcknowledgeSessionUpdate)
                {
                    var sent = message.Json.TryGetProperty("session", out var session) && session.TryGetProperty("instructions", out var text) && text.ValueKind == JsonValueKind.String
                        ? text.GetString()
                        : null;
                    await SendAsync(new { type = "session.updated", event_id = NextEventId(), session = new { id = $"sess_fake_{Index}", type = "realtime", model = Model, instructions = Behavior.AppliedInstructions(sent) } });
                }

                break;

            case "response.create":
            {
                var number = Interlocked.Increment(ref _responseCreates) - 1;
                if (Behavior.ResponseScript(this, number) is { } script)
                {
                    _ = Task.Run(() => StreamInBackgroundAsync(script));
                }

                break;
            }

            case "conversation.item.truncate" when Behavior.AcknowledgeTruncate:
                await SendAsync(new
                {
                    type = "conversation.item.truncated",
                    event_id = NextEventId(),
                    item_id = message.String("item_id"),
                    content_index = message.Int("content_index"),
                    audio_end_ms = message.Int("audio_end_ms"),
                });
                break;
        }
    }

    private async Task StreamInBackgroundAsync(ScriptedResponse script)
    {
        try
        {
            await StreamResponseAsync(script);
        }
        catch (Exception)
        {
            // The socket closed or the hold gate was abandoned; nothing more to send.
        }
    }

    /// <summary>Serializes and sends one server event as a single text frame.</summary>
    public Task SendAsync(object payload) => SendFramesAsync(JsonSerializer.SerializeToUtf8Bytes(payload), int.MaxValue);

    /// <summary>Sends one server event split into several WebSocket frames (exercises fragment assembly).</summary>
    public Task SendFragmentedAsync(object payload, int fragmentBytes) => SendFramesAsync(JsonSerializer.SerializeToUtf8Bytes(payload), fragmentBytes);

    public Task SendRawAsync(string text) => SendFramesAsync(Encoding.UTF8.GetBytes(text), int.MaxValue);

    private async Task SendFramesAsync(byte[] payload, int fragmentBytes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(Wait.Default);
        await _sendLock.WaitAsync(timeout.Token);
        try
        {
            for (var offset = 0; offset < payload.Length; offset += fragmentBytes)
            {
                var length = Math.Min(fragmentBytes, payload.Length - offset);
                var last = offset + length >= payload.Length;
                await _socket.SendAsync(payload.AsMemory(offset, length), WebSocketMessageType.Text, last, timeout.Token);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Drops the TCP connection without a close handshake (abrupt provider failure).</summary>
    public void Abort()
    {
        if (IsClosed)
        {
            return;
        }

        try
        {
            _http.Abort();
        }
        catch (Exception)
        {
            // The request already completed; there is nothing left to abort.
        }
    }

    /// <summary>Starts a normal close handshake from the provider side.</summary>
    public Task CloseAsync() => CloseOutputAsync();

    private async Task CloseOutputAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _sendLock.WaitAsync(timeout.Token);
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fake provider closing", timeout.Token);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.WrittenSpan) : "{\"type\":\"(binary)\"}";
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void SetLastItem(string itemId)
    {
        lock (_gate)
        {
            _lastItemId = itemId;
        }
    }
}

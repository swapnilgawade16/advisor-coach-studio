using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>
/// Scripted WebSocket double. Receives replay queued frames (split across calls when the caller's buffer is smaller
/// than a frame). Sends are recorded, can be delayed or blocked, and overlapping SendAsync calls are detected.
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Queue<Frame> _frames = new();
    private readonly ConcurrentQueue<byte[]> _sent = new();
    private volatile TaskCompletionSource _sendGate = CompletedGate();
    private int _frameOffset;
    private int _inFlightSends;
    private int _maxConcurrentSends;
    private int _sendsStarted;
    private WebSocketState _state = WebSocketState.Open;

    private sealed record Frame(byte[] Data, WebSocketMessageType Type, bool EndOfMessage);

    public TimeSpan SendDelay { get; set; }

    public Exception? SendFailure { get; set; }

    public int ReceiveCalls { get; private set; }

    public int MaxConcurrentSends => Volatile.Read(ref _maxConcurrentSends);

    public int SendsStarted => Volatile.Read(ref _sendsStarted);

    public IReadOnlyList<byte[]> Sent => _sent.ToArray();

    public IReadOnlyList<string> SentText => _sent.Select(payload => Encoding.UTF8.GetString(payload)).ToArray();

    public WebSocketCloseStatus? ClosedOutputWith { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => ClosedOutputWith;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => _state;

    public override string? SubProtocol => "acs.v1";

    public FakeWebSocket EnqueueText(string text, bool endOfMessage = true) =>
        Enqueue(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage);

    public FakeWebSocket Enqueue(byte[] data, WebSocketMessageType type, bool endOfMessage)
    {
        _frames.Enqueue(new Frame(data, type, endOfMessage));
        return this;
    }

    public FakeWebSocket EnqueueClose() => Enqueue([], WebSocketMessageType.Close, endOfMessage: true);

    public void BlockSends() => _sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseSends() => _sendGate.TrySetResult();

    public async Task WaitForSentAsync(int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (_sent.Count < count)
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    public async Task WaitForSendsStartedAsync(int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (SendsStarted < count)
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReceiveCalls++;
        if (!_frames.TryPeek(out var frame) || frame.Type == WebSocketMessageType.Close)
        {
            _frames.TryDequeue(out _);
            _state = WebSocketState.CloseReceived;
            return ValueTask.FromResult(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }

        var count = Math.Min(buffer.Length, frame.Data.Length - _frameOffset);
        frame.Data.AsSpan(_frameOffset, count).CopyTo(buffer.Span);
        _frameOffset += count;
        var frameFinished = _frameOffset == frame.Data.Length;
        if (frameFinished)
        {
            _frames.Dequeue();
            _frameOffset = 0;
        }

        return ValueTask.FromResult(new ValueWebSocketReceiveResult(count, frame.Type, frameFinished && frame.EndOfMessage));
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken);
        return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        RecordConcurrency(Interlocked.Increment(ref _inFlightSends));
        Interlocked.Increment(ref _sendsStarted);
        try
        {
            await _sendGate.Task.WaitAsync(cancellationToken);
            if (SendDelay > TimeSpan.Zero)
            {
                await Task.Delay(SendDelay, cancellationToken);
            }

            if (SendFailure is { } failure)
            {
                throw failure;
            }

            _sent.Enqueue(buffer.ToArray());
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightSends);
        }
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedOutputWith = closeStatus;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedOutputWith = closeStatus;
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Abort() => _state = WebSocketState.Aborted;

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
        GC.SuppressFinalize(this);
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private void RecordConcurrency(int inFlight)
    {
        var observed = Volatile.Read(ref _maxConcurrentSends);
        while (inFlight > observed)
        {
            var previous = Interlocked.CompareExchange(ref _maxConcurrentSends, inFlight, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}

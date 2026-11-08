using System.Net.WebSockets;

namespace AdvisorCoachStudio.Web.Infrastructure.WebSockets;

/// <summary>
/// The only writer for a WebSocket. All sends go through one queue and one loop, so SendAsync is never called
/// concurrently. Ordinary messages are bounded by count and by the approximate duration of queued audio; critical
/// messages (final status, errors) are always accepted so they cannot be lost to a full queue.
/// </summary>
public sealed class SerializedWebSocketSender : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly int _maxQueuedMessages;
    private readonly int _maxQueuedAudioMs;
    private readonly Lock _gate = new();
    private readonly LinkedList<Outgoing> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private bool _completed;
    private int _queuedAudioMs;
    private long _queuedBytes;

    private sealed record Outgoing(ReadOnlyMemory<byte> Payload, int AudioMs, TaskCompletionSource? Sent);

    public SerializedWebSocketSender(WebSocket socket, int maxQueuedMessages, int maxQueuedAudioMs)
    {
        _socket = socket;
        _maxQueuedMessages = maxQueuedMessages;
        _maxQueuedAudioMs = maxQueuedAudioMs;
        _loop = Task.Run(RunAsync);
    }

    public int QueuedAudioMs { get { lock (_gate) { return _queuedAudioMs; } } }
    public long QueuedBytes { get { lock (_gate) { return _queuedBytes; } } }
    public Exception? Fault { get; private set; }
    public bool IsFaulted => Fault is not null;

    /// <summary>
    /// Queues a message without waiting. Returns false if the sender is closed or faulted, or (for non-critical
    /// messages) if the message-count or audio-duration budget would be exceeded.
    /// </summary>
    public bool TryEnqueue(ReadOnlyMemory<byte> payload, int audioMs = 0, bool critical = false)
    {
        lock (_gate)
        {
            if (_completed || IsFaulted)
            {
                return false;
            }

            if (!critical && (_queue.Count >= _maxQueuedMessages || (audioMs > 0 && _queuedAudioMs + audioMs > _maxQueuedAudioMs)))
            {
                return false;
            }

            _queue.AddLast(new Outgoing(payload, audioMs, null));
            _queuedAudioMs += audioMs;
            _queuedBytes += payload.Length;
        }

        _signal.Release();
        return true;
    }

    /// <summary>Queues a message and waits until it has been written to the socket.</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_completed || IsFaulted)
            {
                throw new InvalidOperationException("The WebSocket sender is closed.", Fault);
            }

            _queue.AddLast(new Outgoing(payload, 0, sent));
            _queuedBytes += payload.Length;
        }

        _signal.Release();
        await sent.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Discards queued (not yet sent) audio messages, for example when playback is cleared at the end.</summary>
    public int DropQueuedAudio()
    {
        lock (_gate)
        {
            var dropped = 0;
            var node = _queue.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.AudioMs > 0 && node.Value.Sent is null)
                {
                    _queue.Remove(node);
                    _queuedAudioMs -= node.Value.AudioMs;
                    _queuedBytes -= node.Value.Payload.Length;
                    dropped++;
                }

                node = next;
            }

            return dropped;
        }
    }

    /// <summary>Stops accepting messages, drains what is queued (bounded), then closes the output side.</summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string description, TimeSpan drainTimeout)
    {
        lock (_gate)
        {
            _completed = true;
        }

        _signal.Release();
        try
        {
            await _loop.WaitAsync(drainTimeout);
        }
        catch (TimeoutException)
        {
            await _cts.CancelAsync();
        }
        catch (Exception)
        {
            // Loop faults are surfaced through Fault.
        }

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(status, description, timeout.Token);
            }
            catch (Exception)
            {
                // The peer may already be gone; closing is best effort.
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_cts.Token);
                Outgoing item;
                lock (_gate)
                {
                    if (_queue.First is null)
                    {
                        if (_completed)
                        {
                            return;
                        }

                        continue;
                    }

                    item = _queue.First.Value;
                    _queue.RemoveFirst();
                }

                try
                {
                    if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await _socket.SendAsync(item.Payload, WebSocketMessageType.Text, endOfMessage: true, _cts.Token);
                    }

                    item.Sent?.TrySetResult();
                }
                catch (Exception ex)
                {
                    item.Sent?.TrySetException(ex);
                    throw;
                }
                finally
                {
                    lock (_gate)
                    {
                        _queuedAudioMs -= item.AudioMs;
                        _queuedBytes -= item.Payload.Length;
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            FailPending(ex);
        }
        catch (Exception ex)
        {
            Fault = ex;
            FailPending(ex);
        }
    }

    private void FailPending(Exception ex)
    {
        List<Outgoing> pending;
        lock (_gate)
        {
            _completed = true;
            pending = [.. _queue];
            _queue.Clear();
            _queuedAudioMs = 0;
            _queuedBytes = 0;
        }

        foreach (var item in pending)
        {
            item.Sent?.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _completed = true;
        }

        await _cts.CancelAsync();
        _signal.Release();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }
}

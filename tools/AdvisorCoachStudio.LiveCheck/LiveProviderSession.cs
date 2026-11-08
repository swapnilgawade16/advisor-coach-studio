using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.WebSockets;

namespace AdvisorCoachStudio.LiveCheck;

/// <summary>
/// One live provider connection. Every received event is validated with the application's ProviderEventParser and
/// applied to an independent <see cref="ConversationState"/>. A simulated microphone streams queued speech, or
/// silence, as real-time 20 ms PCM16 frames: the same framing and pacing the browser uses.
/// </summary>
internal sealed class LiveProviderSession
{
    private const int FrameBytes = 960;
    private readonly WebSocket _socket;
    private readonly SerializedWebSocketSender _sender;
    private readonly Channel<ReceivedEvent> _events = Channel.CreateUnbounded<ReceivedEvent>();
    private readonly ConcurrentQueue<QueuedSpeech> _speech = new();
    private readonly ConcurrentQueue<string> _parserFailures = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly CancellationTokenSource _microphoneCts = new();
    private Task? _receiveLoop;
    private Task? _microphoneLoop;
    private long _microphoneFrames;

    public LiveProviderSession(WebSocket socket)
    {
        _socket = socket;
        _sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 4096, maxQueuedAudioMs: 5000);
    }

    public ConversationState State { get; } = new();

    public TimeSpan Elapsed => _clock.Elapsed;

    public IReadOnlyCollection<string> ParserFailures => _parserFailures;

    public string? CloseReason { get; private set; }

    public long MicrophoneFrames => Interlocked.Read(ref _microphoneFrames);

    public void Start() => _receiveLoop = Task.Run(ReceiveLoopAsync);

    public void StartMicrophone() => _microphoneLoop ??= Task.Run(MicrophoneLoopAsync);

    /// <summary>Queues speech after any speech already queued; the task completes when its last frame has been sent.</summary>
    public Task Speak(byte[] pcm)
    {
        var speech = new QueuedSpeech(pcm);
        _speech.Enqueue(speech);
        return speech.Sent.Task;
    }

    public Task SendAsync(byte[] payload, CancellationToken cancellationToken) => _sender.SendAsync(payload, cancellationToken);

    /// <summary>
    /// Processes events one at a time until <paramref name="condition"/> holds; fails on timeout or a provider error.
    /// The condition is also re-evaluated every 50 ms, so it may depend on time or on the microphone.
    /// </summary>
    public async Task RunUntilAsync(Func<bool> condition, TimeSpan timeout, string description, CancellationToken cancellationToken)
    {
        var deadline = Elapsed + timeout;
        while (true)
        {
            ThrowOnProviderError(description);
            if (condition())
            {
                return;
            }

            if (_events.Reader.TryRead(out var received))
            {
                State.Apply(received);
                continue;
            }

            if (Elapsed >= deadline)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F0} s waiting for {description}.");
            }

            using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tick.CancelAfter(TimeSpan.FromMilliseconds(50));
            try
            {
                if (!await _events.Reader.WaitToReadAsync(tick.Token))
                {
                    throw new InvalidOperationException($"The provider closed the connection while waiting for {description} ({CloseReason ?? "no reason given"}).");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>Keeps processing events for a fixed time, for example while client audio would be playing.</summary>
    public async Task PumpAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var until = Elapsed + duration;
        while (Elapsed < until)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = until - Elapsed;
            timeoutCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            try
            {
                State.Apply(await _events.Reader.ReadAsync(timeoutCts.Token));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
        }

        ThrowOnProviderError("the playback window");
    }

    public async Task CloseAsync()
    {
        await _microphoneCts.CancelAsync();
        await AwaitQuietlyAsync(_microphoneLoop);
        await _sender.CloseAsync(WebSocketCloseStatus.NormalClosure, "live check complete", TimeSpan.FromSeconds(3));
        if (_receiveLoop is not null)
        {
            await Task.WhenAny(_receiveLoop, Task.Delay(TimeSpan.FromSeconds(3)));
        }

        await _receiveCts.CancelAsync();
        await AwaitQuietlyAsync(_receiveLoop);
        await _sender.DisposeAsync();
        _socket.Dispose();
    }

    private void ThrowOnProviderError(string description)
    {
        if (State.PendingErrors.Count > 0)
        {
            var error = State.PendingErrors[0];
            State.PendingErrors.Clear();
            throw new InvalidOperationException($"Provider error while waiting for {description}: {error}");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var message = await WebSocketMessageReader.ReadAsync(_socket, 16 * 1024 * 1024, _receiveCts.Token);
                if (message is null)
                {
                    CloseReason = $"closed by provider: {_socket.CloseStatus} {_socket.CloseStatusDescription}".Trim();
                    return;
                }

                if (message.Value.Type != WebSocketMessageType.Text)
                {
                    continue;
                }

                try
                {
                    _ = ProviderEventParser.Parse(message.Value.Payload);
                }
                catch (ProviderProtocolException ex)
                {
                    _parserFailures.Enqueue($"{ex.EventType}: missing or invalid '{ex.Field}'");
                }
                catch (JsonException)
                {
                    _parserFailures.Enqueue("invalid JSON frame");
                    continue;
                }

                using var document = JsonDocument.Parse(message.Value.Payload);
                await _events.Writer.WriteAsync(new ReceivedEvent(document.RootElement.Clone(), Elapsed), _receiveCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            CloseReason = ex.Message;
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private async Task MicrophoneLoopAsync()
    {
        var silence = new byte[FrameBytes];
        var started = Elapsed;
        QueuedSpeech? current = null;
        var offset = 0;
        try
        {
            while (!_microphoneCts.IsCancellationRequested)
            {
                if (current is null && _speech.TryDequeue(out var next))
                {
                    current = next;
                    offset = 0;
                }

                var frame = silence;
                if (current is not null)
                {
                    frame = new byte[FrameBytes];
                    var length = Math.Min(FrameBytes, current.Pcm.Length - offset);
                    current.Pcm.AsSpan(offset, length).CopyTo(frame);
                    offset += length;
                }

                await _sender.SendAsync(ProviderClientEvents.AppendAudio(Convert.ToBase64String(frame)), _microphoneCts.Token);
                if (current is not null && offset >= current.Pcm.Length)
                {
                    current.Sent.TrySetResult();
                    current = null;
                }

                var frames = Interlocked.Increment(ref _microphoneFrames);
                var wait = started + TimeSpan.FromMilliseconds(frames * 20) - Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _microphoneCts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or WebSocketException)
        {
        }
        finally
        {
            current?.Sent.TrySetCanceled();
            while (_speech.TryDequeue(out var pending))
            {
                pending.Sent.TrySetCanceled();
            }
        }
    }

    private sealed class QueuedSpeech(byte[] pcm)
    {
        public byte[] Pcm { get; } = pcm;

        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (Exception)
        {
            // Background loop failures are reported through CloseReason or the checks.
        }
    }
}

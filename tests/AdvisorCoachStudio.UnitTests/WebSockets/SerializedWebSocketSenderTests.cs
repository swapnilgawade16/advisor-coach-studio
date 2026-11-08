using System.Net.WebSockets;
using System.Text;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Infrastructure.WebSockets;

namespace AdvisorCoachStudio.UnitTests.WebSockets;

public sealed class SerializedWebSocketSenderTests
{
    [Fact]
    public async Task FakeSocketDetectsOverlappingSends()
    {
        var socket = new FakeWebSocket { SendDelay = TimeSpan.FromMilliseconds(50) };
        var ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(
            socket.SendAsync(new ArraySegment<byte>(Payload("a")), WebSocketMessageType.Text, endOfMessage: true, ct),
            socket.SendAsync(new ArraySegment<byte>(Payload("b")), WebSocketMessageType.Text, endOfMessage: true, ct));

        Assert.Equal(2, socket.MaxConcurrentSends);
    }

    [Fact]
    public async Task NeverCallsSendAsyncConcurrently()
    {
        var socket = new FakeWebSocket { SendDelay = TimeSpan.FromMilliseconds(1) };
        var ct = TestContext.Current.CancellationToken;
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 256, maxQueuedAudioMs: 100_000);

        var producers = Enumerable.Range(0, 8).Select(producer => Task.Run(
            async () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    var payload = Payload($"p{producer}-m{i}");
                    if (i % 2 == 0)
                    {
                        await sender.SendAsync(payload, ct);
                    }
                    else
                    {
                        Assert.True(sender.TryEnqueue(payload, audioMs: 20));
                    }
                }
            },
            ct));
        await Task.WhenAll(producers);
        await socket.WaitForSentAsync(80, ct);

        Assert.Equal(1, socket.MaxConcurrentSends);
        var sent = socket.SentText;
        Assert.Equal(80, sent.Count);
        for (var producer = 0; producer < 8; producer++)
        {
            var prefix = $"p{producer}-";
            var expected = Enumerable.Range(0, 10).Select(i => $"{prefix}m{i}");
            Assert.Equal(expected, sent.Where(text => text.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task PreservesEnqueueOrder()
    {
        var socket = new FakeWebSocket();
        var ct = TestContext.Current.CancellationToken;
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 200, maxQueuedAudioMs: 10_000);
        var expected = Enumerable.Range(0, 100).Select(i => $"message-{i}").ToList();

        foreach (var text in expected.Take(50))
        {
            Assert.True(sender.TryEnqueue(Payload(text)));
        }

        foreach (var text in expected.Skip(50))
        {
            await sender.SendAsync(Payload(text), ct);
        }

        await socket.WaitForSentAsync(100, ct);
        Assert.Equal(expected, socket.SentText);
    }

    [Fact]
    public async Task SendAsyncCompletesOnlyAfterThePayloadWasWritten()
    {
        var socket = new FakeWebSocket();
        var ct = TestContext.Current.CancellationToken;
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 8, maxQueuedAudioMs: 1000);
        socket.BlockSends();

        var sending = sender.SendAsync(Payload("blocked"), ct);
        await socket.WaitForSendsStartedAsync(1, ct);
        Assert.False(sending.IsCompleted);

        socket.ReleaseSends();
        await sending;

        Assert.Equal(new[] { "blocked" }, socket.SentText);
    }

    [Fact]
    public async Task TryEnqueueRefusesAudioBeyondTheQueuedDurationBudget()
    {
        var socket = new FakeWebSocket();
        var ct = TestContext.Current.CancellationToken;
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 100, maxQueuedAudioMs: 1000);
        socket.BlockSends();

        Assert.True(sender.TryEnqueue(Payload("audio-1"), audioMs: 400));
        Assert.True(sender.TryEnqueue(Payload("audio-2"), audioMs: 400));
        Assert.False(sender.TryEnqueue(Payload("audio-3"), audioMs: 400));
        Assert.True(sender.TryEnqueue(Payload("audio-4"), audioMs: 200));
        Assert.False(sender.TryEnqueue(Payload("audio-5"), audioMs: 1));
        Assert.True(sender.TryEnqueue(Payload("control"), audioMs: 0));
        Assert.Equal(1000, sender.QueuedAudioMs);

        socket.ReleaseSends();
        await socket.WaitForSentAsync(4, ct);
        await WaitUntilAsync(() => sender.QueuedAudioMs == 0, ct);

        Assert.Equal(new[] { "audio-1", "audio-2", "audio-4", "control" }, socket.SentText);
        Assert.True(sender.TryEnqueue(Payload("audio-6"), audioMs: 1000));
    }

    [Fact]
    public async Task TryEnqueueRefusesWhenTheMessageQueueIsFull()
    {
        var socket = new FakeWebSocket();
        var ct = TestContext.Current.CancellationToken;
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 2, maxQueuedAudioMs: 10_000);
        socket.BlockSends();

        Assert.True(sender.TryEnqueue(Payload("in-flight")));
        await socket.WaitForSendsStartedAsync(1, ct);
        Assert.True(sender.TryEnqueue(Payload("queued-1")));
        Assert.True(sender.TryEnqueue(Payload("queued-2")));

        Assert.False(sender.TryEnqueue(Payload("overflow")));
        socket.ReleaseSends();
        await socket.WaitForSentAsync(3, ct);
        Assert.Equal(new[] { "in-flight", "queued-1", "queued-2" }, socket.SentText);
    }

    [Fact]
    public async Task CloseAsyncDrainsQueuedMessagesThenClosesTheOutput()
    {
        var socket = new FakeWebSocket { SendDelay = TimeSpan.FromMilliseconds(5) };
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 16, maxQueuedAudioMs: 1000);
        Assert.True(sender.TryEnqueue(Payload("one")));
        Assert.True(sender.TryEnqueue(Payload("two")));
        Assert.True(sender.TryEnqueue(Payload("three")));

        await sender.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "one", "two", "three" }, socket.SentText);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedOutputWith);
        Assert.False(sender.TryEnqueue(Payload("after-close")));
        Assert.False(sender.TryEnqueue(Payload("critical-after-close"), critical: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Payload("after-close"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASendFailureFaultsTheSenderAndRejectsFurtherMessages()
    {
        var socket = new FakeWebSocket { SendFailure = new WebSocketException("Synthetic send failure.") };
        var ct = TestContext.Current.CancellationToken;
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 16, maxQueuedAudioMs: 1000);

        await Assert.ThrowsAsync<WebSocketException>(() => sender.SendAsync(Payload("fails"), ct));
        await WaitUntilAsync(() => sender.IsFaulted, ct);

        Assert.IsType<WebSocketException>(sender.Fault);
        Assert.False(sender.TryEnqueue(Payload("rejected")));
        Assert.False(sender.TryEnqueue(Payload("rejected-critical"), critical: true));
        var closed = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Payload("rejected"), ct));
        Assert.Same(sender.Fault, closed.InnerException);
    }

    [Fact]
    public async Task CriticalMessagesBypassTheCountAndAudioBudgetsWithoutReordering()
    {
        var socket = new FakeWebSocket();
        var ct = TestContext.Current.CancellationToken;
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 1, maxQueuedAudioMs: 100);
        socket.BlockSends();
        Assert.True(sender.TryEnqueue(Payload("audio-in-flight"), audioMs: 100));
        await socket.WaitForSendsStartedAsync(1, ct);
        Assert.True(sender.TryEnqueue(Payload("control-queued")));

        Assert.False(sender.TryEnqueue(Payload("control-overflow")));
        Assert.False(sender.TryEnqueue(Payload("audio-overflow"), audioMs: 20));
        Assert.True(sender.TryEnqueue(Payload("session.ended"), critical: true));
        Assert.True(sender.TryEnqueue(Payload("critical-audio"), audioMs: 20, critical: true));
        Assert.Equal(120, sender.QueuedAudioMs);

        socket.ReleaseSends();
        await socket.WaitForSentAsync(4, ct);

        Assert.Equal(new[] { "audio-in-flight", "control-queued", "session.ended", "critical-audio" }, socket.SentText);
        Assert.Equal(1, socket.MaxConcurrentSends);
    }

    [Fact]
    public async Task DropQueuedAudioRemovesOnlyUnsentAudio()
    {
        var socket = new FakeWebSocket();
        var ct = TestContext.Current.CancellationToken;
        await using var sender = new SerializedWebSocketSender(socket, maxQueuedMessages: 16, maxQueuedAudioMs: 1000);
        socket.BlockSends();
        Assert.True(sender.TryEnqueue(Payload("audio-in-flight"), audioMs: 100));
        await socket.WaitForSendsStartedAsync(1, ct);
        Assert.True(sender.TryEnqueue(Payload("audio-queued-1"), audioMs: 200));
        Assert.True(sender.TryEnqueue(Payload("transcript.final")));
        Assert.True(sender.TryEnqueue(Payload("audio-queued-2"), audioMs: 300));
        Assert.True(sender.TryEnqueue(Payload("playback.clear"), critical: true));
        var awaited = sender.SendAsync(Payload("awaited-control"), ct);

        Assert.Equal(2, sender.DropQueuedAudio());

        Assert.Equal(100, sender.QueuedAudioMs);
        Assert.Equal(0, sender.DropQueuedAudio());
        socket.ReleaseSends();
        await awaited;
        await socket.WaitForSentAsync(4, ct);
        await WaitUntilAsync(() => sender.QueuedAudioMs == 0, ct);
        Assert.Equal(new[] { "audio-in-flight", "transcript.final", "playback.clear", "awaited-control" }, socket.SentText);
    }

    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}

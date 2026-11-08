using System.Net.WebSockets;
using System.Text;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Infrastructure.WebSockets;

namespace AdvisorCoachStudio.UnitTests.WebSockets;

public sealed class WebSocketMessageReaderTests
{
    [Fact]
    public async Task AssemblesAFragmentedMessage()
    {
        var socket = new FakeWebSocket()
            .EnqueueText("{\"v\":1,", endOfMessage: false)
            .EnqueueText("\"type\":", endOfMessage: false)
            .EnqueueText("\"heartbeat\"}", endOfMessage: true);

        var message = await WebSocketMessageReader.ReadAsync(socket, 1024, TestContext.Current.CancellationToken);

        Assert.NotNull(message);
        Assert.Equal(WebSocketMessageType.Text, message.Value.Type);
        Assert.Equal("{\"v\":1,\"type\":\"heartbeat\"}", Encoding.UTF8.GetString(message.Value.Payload.Span));
        Assert.Equal(3, socket.ReceiveCalls);
    }

    [Fact]
    public async Task ReadsConsecutiveMessagesIndependently()
    {
        var socket = new FakeWebSocket()
            .EnqueueText("first-", endOfMessage: false)
            .EnqueueText("message", endOfMessage: true)
            .EnqueueText("second-message", endOfMessage: true);
        var ct = TestContext.Current.CancellationToken;

        var first = await WebSocketMessageReader.ReadAsync(socket, 64, ct);
        var second = await WebSocketMessageReader.ReadAsync(socket, 64, ct);

        Assert.Equal("first-message", Encoding.UTF8.GetString(first!.Value.Payload.Span));
        Assert.Equal("second-message", Encoding.UTF8.GetString(second!.Value.Payload.Span));
    }

    [Fact]
    public async Task AssemblesAMessageLargerThanOneReceiveBuffer()
    {
        var payload = Enumerable.Range(0, 40_000).Select(i => (byte)('a' + (i % 26))).ToArray();
        var socket = new FakeWebSocket().Enqueue(payload, WebSocketMessageType.Text, endOfMessage: true);

        var message = await WebSocketMessageReader.ReadAsync(socket, 64 * 1024, TestContext.Current.CancellationToken);

        Assert.Equal(payload, message!.Value.Payload.ToArray());
        Assert.True(socket.ReceiveCalls > 1, "A 40 KB message cannot arrive in a single 16 KB receive.");
    }

    [Fact]
    public async Task PreservesTheBinaryMessageType()
    {
        var socket = new FakeWebSocket().Enqueue([1, 2, 3], WebSocketMessageType.Binary, endOfMessage: true);

        var message = await WebSocketMessageReader.ReadAsync(socket, 16, TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketMessageType.Binary, message!.Value.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, message.Value.Payload.ToArray());
    }

    [Fact]
    public async Task ReturnsNullWhenThePeerCloses()
    {
        var socket = new FakeWebSocket().EnqueueClose();

        Assert.Null(await WebSocketMessageReader.ReadAsync(socket, 1024, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReturnsNullWhenThePeerClosesMidMessage()
    {
        var socket = new FakeWebSocket().EnqueueText("{\"v\":1,", endOfMessage: false).EnqueueClose();

        Assert.Null(await WebSocketMessageReader.ReadAsync(socket, 1024, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThrowsWhenTheAssembledMessageExceedsTheLimit()
    {
        var socket = new FakeWebSocket()
            .EnqueueText(new string('a', 10), endOfMessage: false)
            .EnqueueText(new string('b', 10), endOfMessage: true);

        var error = await Assert.ThrowsAsync<WebSocketMessageTooLargeException>(
            () => WebSocketMessageReader.ReadAsync(socket, 16, TestContext.Current.CancellationToken));

        Assert.Equal(16, error.Limit);
    }

    [Fact]
    public async Task AcceptsAMessageExactlyAtTheLimit()
    {
        var socket = new FakeWebSocket()
            .EnqueueText(new string('a', 8), endOfMessage: false)
            .EnqueueText(new string('b', 8), endOfMessage: true);

        var message = await WebSocketMessageReader.ReadAsync(socket, 16, TestContext.Current.CancellationToken);

        Assert.Equal(16, message!.Value.Payload.Length);
    }
}

using System.Buffers;
using System.Net.WebSockets;

namespace AdvisorCoachStudio.Web.Infrastructure.WebSockets;

public sealed class WebSocketMessageTooLargeException(int limit) : Exception($"WebSocket message exceeded {limit} bytes.")
{
    public int Limit { get; } = limit;
}

public readonly record struct WebSocketMessage(WebSocketMessageType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads one complete WebSocket message, assembling fragments until EndOfMessage.
/// One receive call is never assumed to equal one JSON event.
/// </summary>
public static class WebSocketMessageReader
{
    /// <summary>Returns null when the peer sends a close frame.</summary>
    public static async Task<WebSocketMessage?> ReadAsync(WebSocket socket, int maxMessageBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxMessageBytes, 16 * 1024));
        var writer = new ArrayBufferWriter<byte>(Math.Min(maxMessageBytes, 4 * 1024));
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (writer.WrittenCount + result.Count > maxMessageBytes)
                {
                    throw new WebSocketMessageTooLargeException(maxMessageBytes);
                }

                writer.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    return new WebSocketMessage(result.MessageType, writer.WrittenMemory);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

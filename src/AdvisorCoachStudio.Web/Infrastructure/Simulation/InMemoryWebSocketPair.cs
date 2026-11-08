using System.IO.Pipelines;
using System.Net.WebSockets;

namespace AdvisorCoachStudio.Web.Infrastructure.Simulation;

/// <summary>Creates a connected client/server WebSocket pair over in-memory pipes (no network).</summary>
public static class InMemoryWebSocketPair
{
    public static (WebSocket Client, WebSocket Server) Create()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var clientStream = new DuplexPipeStream(serverToClient.Reader, clientToServer.Writer);
        var serverStream = new DuplexPipeStream(clientToServer.Reader, serverToClient.Writer);
        var client = WebSocket.CreateFromStream(clientStream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        var server = WebSocket.CreateFromStream(serverStream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        return (client, server);
    }

    private sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _read = reader.AsStream();
        private readonly Stream _write = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);
        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

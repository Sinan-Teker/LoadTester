using System.Net.Sockets;

namespace LoadTester.Core.Engine;

/// <summary>Counts bytes on the wire (headers and TLS overhead included) for every connection the engine opens.</summary>
internal sealed class TrafficCounter
{
    private long _sent;
    private long _received;

    public long Sent => Interlocked.Read(ref _sent);
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Used as <see cref="SocketsHttpHandler.ConnectCallback"/>.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new CountingStream(new NetworkStream(socket, ownsSocket: true), this);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class CountingStream(Stream inner, TrafficCounter counter) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => CountReceived(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => CountReceived(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            CountReceived(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            CountSent(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            CountSent(buffer.Length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            CountSent(buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        private int CountReceived(int count)
        {
            Interlocked.Add(ref counter._received, count);
            return count;
        }

        private void CountSent(int count) => Interlocked.Add(ref counter._sent, count);
    }
}

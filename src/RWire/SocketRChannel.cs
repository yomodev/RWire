using System.Net.Sockets;

namespace RWire;

/// <summary>
/// IRChannel implementation over a connected TCP socket - the v1
/// transport (docs/spec.md section 2.1). NetworkStream's Read/ReadAsync
/// and Write/WriteAsync are both genuine, independent I/O paths, so
/// this satisfies IRChannel without needing to fake either direction.
///
/// Adding a future channel (named pipe, memory-mapped file) means
/// writing another IRChannel implementation like this one - the frame
/// codec and RConnection above it never need to change.
/// </summary>
public sealed class SocketRChannel : IRChannel
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _disposed;

    public SocketRChannel(TcpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _stream = client.GetStream();
    }

    public int Read(Span<byte> buffer) => _stream.Read(buffer);

    public void Write(ReadOnlySpan<byte> buffer) => _stream.Write(buffer);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stream.ReadAsync(buffer, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stream.WriteAsync(buffer, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _client.Dispose();
    }
}

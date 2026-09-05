using System.Buffers;

namespace RWire;

/// <summary>
/// Drives FrameCodec over an IRChannel with two independent execution
/// paths - synchronous and asynchronous - built from the same wire
/// format (docs/spec.md sections 2.1 and 9). Neither path is derived
/// from the other: the sync path never blocks on a Task, and the
/// async path never risks a sync-over-async deadlock.
/// </summary>
public sealed class RConnection : IDisposable
{
    private readonly IRChannel _channel;
    private uint _nextCorrelationId = 1; // 0 is reserved for server-initiated frames.

    public RConnection(IRChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    /// <summary>Allocates the next client-assigned correlation id.</summary>
    public uint NextCorrelationId() => _nextCorrelationId++;

    // ---------------------------------------------------------------
    // Synchronous path
    // ---------------------------------------------------------------

    public void Send(MsgType msgType, uint correlationId, ReadOnlySpan<byte> payload)
    {
        int total = FrameCodec.TotalSize(payload.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            int written = FrameCodec.EncodeFrame(rented, msgType, correlationId, payload);
            _channel.Write(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public Frame Receive()
    {
        Span<byte> lengthPrefix = stackalloc byte[FrameCodec.LengthPrefixSize];
        ReadExact(lengthPrefix);
        int bodyLength = FrameCodec.DecodeLengthPrefix(lengthPrefix);

        Span<byte> fixedHeader = stackalloc byte[FrameCodec.FixedHeaderSize];
        ReadExact(fixedHeader);
        (MsgType msgType, uint correlationId, int payloadLength) =
            FrameCodec.DecodeFixedHeader(fixedHeader, bodyLength);

        byte[] payloadBuffer = payloadLength == 0
            ? Array.Empty<byte>()
            : ArrayPool<byte>.Shared.Rent(payloadLength);

        if (payloadLength > 0)
        {
            ReadExact(payloadBuffer.AsSpan(0, payloadLength));
        }

        return new Frame(msgType, correlationId, payloadBuffer, payloadLength);
    }

    private void ReadExact(Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = _channel.Read(buffer.Slice(offset));
            if (read == 0)
            {
                throw new EndOfStreamException("Channel closed while reading a frame.");
            }
            offset += read;
        }
    }

    // ---------------------------------------------------------------
    // Asynchronous path
    // ---------------------------------------------------------------

    public async ValueTask SendAsync(
        MsgType msgType, uint correlationId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        int total = FrameCodec.TotalSize(payload.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            int written = FrameCodec.EncodeFrame(rented, msgType, correlationId, payload.Span);
            await _channel.WriteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<Frame> ReceiveAsync(CancellationToken ct = default)
    {
        byte[] lengthPrefixBuffer = ArrayPool<byte>.Shared.Rent(FrameCodec.LengthPrefixSize);
        int bodyLength;
        try
        {
            await ReadExactAsync(lengthPrefixBuffer.AsMemory(0, FrameCodec.LengthPrefixSize), ct)
                .ConfigureAwait(false);
            bodyLength = FrameCodec.DecodeLengthPrefix(
                lengthPrefixBuffer.AsSpan(0, FrameCodec.LengthPrefixSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthPrefixBuffer);
        }

        byte[] fixedHeaderBuffer = ArrayPool<byte>.Shared.Rent(FrameCodec.FixedHeaderSize);
        MsgType msgType;
        uint correlationId;
        int payloadLength;
        try
        {
            await ReadExactAsync(fixedHeaderBuffer.AsMemory(0, FrameCodec.FixedHeaderSize), ct)
                .ConfigureAwait(false);
            (msgType, correlationId, payloadLength) = FrameCodec.DecodeFixedHeader(
                fixedHeaderBuffer.AsSpan(0, FrameCodec.FixedHeaderSize), bodyLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fixedHeaderBuffer);
        }

        byte[] payloadBuffer = payloadLength == 0
            ? Array.Empty<byte>()
            : ArrayPool<byte>.Shared.Rent(payloadLength);

        if (payloadLength > 0)
        {
            await ReadExactAsync(payloadBuffer.AsMemory(0, payloadLength), ct).ConfigureAwait(false);
        }

        return new Frame(msgType, correlationId, payloadBuffer, payloadLength);
    }

    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _channel.ReadAsync(buffer.Slice(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Channel closed while reading a frame.");
            }
            offset += read;
        }
    }

    public void Dispose() => _channel.Dispose();
}

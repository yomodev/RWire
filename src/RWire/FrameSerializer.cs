using System.Buffers;

namespace RWire;

/// <summary>
/// Serializes a Frame (a single decoded protocol message - MsgType +
/// CorrelationId + Payload, docs/spec.md section 4.1) to and from a
/// standalone byte array or file, independent of any live connection.
/// Useful for capturing a real exchanged message for later inspection,
/// building a fixture from a captured message for a test, or replaying
/// a message without a live R process.
///
/// The byte array produced is exactly what would cross the wire for
/// that one frame ([Length(4)][MsgType(1)][CorrelationId(4)]
/// [PayloadLen(4)][Payload(N)]) - FrameCodec.EncodeFrame/DecodeFrame's
/// logic reused unchanged, not a separate format. A file saved with
/// SaveToFile can be read back by any tool that understands RWire's
/// frame format, not just this method.
/// </summary>
public static class FrameSerializer
{
    /// <summary>Encodes frame into a standalone byte array.</summary>
    public static byte[] ToByteArray(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        byte[] buffer = new byte[FrameCodec.TotalSize(frame.Payload.Length)];
        FrameCodec.EncodeFrame(buffer, frame.MsgType, frame.CorrelationId, frame.Payload.Span);
        return buffer;
    }

    /// <summary>
    /// Decodes a byte array produced by ToByteArray (or captured
    /// directly off the wire) back into a Frame. The returned Frame
    /// owns a pooled buffer like any other Frame - dispose it (a
    /// `using` statement is enough) when done.
    /// </summary>
    public static Frame FromByteArray(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        int declaredBodyLength = FrameCodec.DecodeLengthPrefix(bytes);
        int expectedTotalLength = FrameCodec.LengthPrefixSize + declaredBodyLength;
        if (bytes.Length != expectedTotalLength)
        {
            throw new InvalidDataException(
                $"Byte array length ({bytes.Length}) doesn't match the frame's declared " +
                $"total length ({expectedTotalLength}) - the data is truncated, has trailing " +
                "bytes, or isn't a single encoded frame.");
        }

        ReadOnlySpan<byte> fixedHeader = bytes.AsSpan(FrameCodec.LengthPrefixSize, FrameCodec.FixedHeaderSize);
        (MsgType msgType, uint correlationId, int payloadLength) =
            FrameCodec.DecodeFixedHeader(fixedHeader, declaredBodyLength);

        // Array.Empty<byte>() for a zero-length payload, matching how
        // Frame.Dispose() already knows not to return a zero-length
        // buffer to the pool (see Frame.cs) - only a real pool rental
        // should ever be returned to ArrayPool.Shared.
        byte[] payloadBuffer = payloadLength == 0
            ? Array.Empty<byte>()
            : ArrayPool<byte>.Shared.Rent(payloadLength);

        if (payloadLength > 0)
        {
            bytes.AsSpan(FrameCodec.LengthPrefixSize + FrameCodec.FixedHeaderSize, payloadLength)
                .CopyTo(payloadBuffer);
        }

        return new Frame(msgType, correlationId, payloadBuffer, payloadLength);
    }

    /// <summary>Encodes frame and writes it to path, overwriting any existing file.</summary>
    public static void SaveToFile(Frame frame, string path) => File.WriteAllBytes(path, ToByteArray(frame));

    /// <summary>
    /// Reads path and decodes it back into a Frame. The returned Frame
    /// owns a pooled buffer like any other Frame - dispose it (a
    /// `using` statement is enough) when done.
    /// </summary>
    public static Frame LoadFromFile(string path) => FromByteArray(File.ReadAllBytes(path));
}

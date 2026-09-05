using System.Buffers.Binary;

namespace RWire;

/// <summary>
/// Pure, channel-agnostic frame encode/decode (docs/spec.md section
/// 4.1). Wire format, little-endian throughout:
///
///   [Length(4)][MsgType(1)][CorrelationId(4)][PayloadLen(4)][Payload(N)]
///
/// Length is the byte count of everything after the Length field
/// itself (FixedHeaderSize + N). PayloadLen is redundant with Length
/// by construction and is cross-checked on decode as a corruption/
/// desync guard, not trusted blindly.
///
/// This type knows nothing about IRChannel, sockets, or any other
/// transport - it only operates on spans - so it's identical
/// regardless of which channel eventually carries the bytes.
/// </summary>
public static class FrameCodec
{
    /// <summary>Bytes in the outer length prefix.</summary>
    public const int LengthPrefixSize = 4;

    /// <summary>Bytes in the fixed part of a frame body: MsgType(1) + CorrelationId(4) + PayloadLen(4).</summary>
    public const int FixedHeaderSize = 1 + 4 + 4;

    /// <summary>Total encoded size (length prefix + fixed header + payload) for a payload of the given length.</summary>
    public static int TotalSize(int payloadLength) => LengthPrefixSize + FixedHeaderSize + payloadLength;

    /// <summary>
    /// Encodes a full frame (length prefix + fixed header + payload)
    /// into destination. destination must be at least
    /// TotalSize(payload.Length) bytes. Returns the number of bytes
    /// written.
    /// </summary>
    public static int EncodeFrame(
        Span<byte> destination, MsgType msgType, uint correlationId, ReadOnlySpan<byte> payload)
    {
        int total = TotalSize(payload.Length);
        if (destination.Length < total)
        {
            throw new ArgumentException(
                $"Destination too small: need {total} bytes, got {destination.Length}.",
                nameof(destination));
        }

        int bodyLength = FixedHeaderSize + payload.Length;
        BinaryPrimitives.WriteInt32LittleEndian(destination, bodyLength);

        Span<byte> header = destination.Slice(LengthPrefixSize, FixedHeaderSize);
        header[0] = (byte)msgType;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(1), correlationId);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(5), payload.Length);

        payload.CopyTo(destination.Slice(LengthPrefixSize + FixedHeaderSize));

        return total;
    }

    /// <summary>
    /// Reads the outer Length field: the byte count of the fixed
    /// header + payload that follows. Throws InvalidDataException if
    /// the value is smaller than the minimum possible body
    /// (FixedHeaderSize), which indicates a desynced or corrupt stream
    /// rather than a merely-empty frame.
    /// </summary>
    public static int DecodeLengthPrefix(ReadOnlySpan<byte> lengthPrefix)
    {
        if (lengthPrefix.Length < LengthPrefixSize)
        {
            throw new ArgumentException(
                "Need at least 4 bytes to decode the length prefix.", nameof(lengthPrefix));
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (length < FixedHeaderSize)
        {
            throw new InvalidDataException(
                $"Frame length {length} is smaller than the minimum fixed header size " +
                $"({FixedHeaderSize}) - stream is desynced or corrupt.");
        }

        return length;
    }

    /// <summary>
    /// Decodes the fixed header (MsgType + CorrelationId + PayloadLen)
    /// and cross-checks PayloadLen against expectedBodyLength (the
    /// value returned by DecodeLengthPrefix for this same frame).
    /// Throws InvalidDataException on an unknown MsgType byte or on
    /// any inconsistency between PayloadLen and the frame's actual
    /// length - both indicate a desynced or corrupt stream, and the
    /// caller must not attempt to keep reading from this connection as
    /// if nothing happened.
    /// </summary>
    public static (MsgType MsgType, uint CorrelationId, int PayloadLength) DecodeFixedHeader(
        ReadOnlySpan<byte> fixedHeader, int expectedBodyLength)
    {
        if (fixedHeader.Length < FixedHeaderSize)
        {
            throw new ArgumentException(
                $"Need at least {FixedHeaderSize} bytes to decode the fixed header.",
                nameof(fixedHeader));
        }

        byte rawMsgType = fixedHeader[0];
        if (!Enum.IsDefined(typeof(MsgType), rawMsgType))
        {
            throw new InvalidDataException($"Unknown MsgType byte: 0x{rawMsgType:X2}.");
        }

        var msgType = (MsgType)rawMsgType;
        uint correlationId = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.Slice(1, 4));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(fixedHeader.Slice(5, 4));

        if (payloadLength < 0 || FixedHeaderSize + payloadLength != expectedBodyLength)
        {
            throw new InvalidDataException(
                $"PayloadLen ({payloadLength}) is inconsistent with the frame's declared body " +
                $"length ({expectedBodyLength}) - stream is desynced or corrupt.");
        }

        return (msgType, correlationId, payloadLength);
    }
}

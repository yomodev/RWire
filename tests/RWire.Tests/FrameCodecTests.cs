using System.Buffers.Binary;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Pure unit tests for FrameCodec (docs/spec.md section 12.1) - no R
/// process involved, operates purely on byte spans.
/// </summary>
public class FrameCodecTests
{
    [Theory]
    [InlineData(MsgType.Ping, 0)]
    [InlineData(MsgType.Hello, 5)]
    [InlineData(MsgType.Result, 1000)]
    [InlineData(MsgType.Error, 1)]
    public void EncodeThenDecode_RoundTripsExactly(MsgType msgType, int payloadSize)
    {
        byte[] payload = new byte[payloadSize];
        new Random(42).NextBytes(payload);

        int total = FrameCodec.TotalSize(payload.Length);
        byte[] buffer = new byte[total];

        int written = FrameCodec.EncodeFrame(buffer, msgType, correlationId: 7, payload);
        Assert.Equal(total, written);

        int bodyLength = FrameCodec.DecodeLengthPrefix(buffer.AsSpan(0, FrameCodec.LengthPrefixSize));
        (MsgType decodedMsgType, uint correlationId, int payloadLength) = FrameCodec.DecodeFixedHeader(
            buffer.AsSpan(FrameCodec.LengthPrefixSize, FrameCodec.FixedHeaderSize), bodyLength);

        Assert.Equal(msgType, decodedMsgType);
        Assert.Equal(7u, correlationId);
        Assert.Equal(payload.Length, payloadLength);

        byte[] decodedPayload = buffer
            .AsSpan(FrameCodec.LengthPrefixSize + FrameCodec.FixedHeaderSize, payloadLength)
            .ToArray();
        Assert.Equal(payload, decodedPayload);
    }

    [Fact]
    public void DecodeLengthPrefix_SmallerThanMinimumBody_Throws()
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 3); // smaller than FixedHeaderSize (9)

        Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeLengthPrefix(buffer));
    }

    [Fact]
    public void DecodeFixedHeader_UnknownMsgType_Throws()
    {
        byte[] header = new byte[FrameCodec.FixedHeaderSize];
        header[0] = 0xFF; // not a defined MsgType
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), 0);

        Assert.Throws<InvalidDataException>(
            () => FrameCodec.DecodeFixedHeader(header, expectedBodyLength: FrameCodec.FixedHeaderSize));
    }

    [Fact]
    public void DecodeFixedHeader_PayloadLengthInconsistentWithBodyLength_Throws()
    {
        byte[] header = new byte[FrameCodec.FixedHeaderSize];
        header[0] = (byte)MsgType.Ping;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), 100); // claims 100 payload bytes

        // expectedBodyLength says there's no payload at all - inconsistent.
        Assert.Throws<InvalidDataException>(
            () => FrameCodec.DecodeFixedHeader(header, expectedBodyLength: FrameCodec.FixedHeaderSize));
    }

    [Fact]
    public void DecodeFixedHeader_NegativePayloadLength_Throws()
    {
        byte[] header = new byte[FrameCodec.FixedHeaderSize];
        header[0] = (byte)MsgType.Ping;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), -1);

        Assert.Throws<InvalidDataException>(
            () => FrameCodec.DecodeFixedHeader(header, expectedBodyLength: FrameCodec.FixedHeaderSize - 1));
    }

    [Fact]
    public void EncodeFrame_DestinationTooSmall_Throws()
    {
        byte[] tooSmall = new byte[5];
        Assert.Throws<ArgumentException>(
            () => FrameCodec.EncodeFrame(tooSmall, MsgType.Ping, 1, new byte[10]));
    }

    [Fact]
    public void TotalSize_MatchesLengthPrefixPlusFixedHeaderPlusPayload()
    {
        Assert.Equal(
            FrameCodec.LengthPrefixSize + FrameCodec.FixedHeaderSize + 42,
            FrameCodec.TotalSize(42));
    }
}

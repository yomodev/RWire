using System.Buffers.Binary;
using AwesomeAssertions;
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
        written.Should().Be(total);

        int bodyLength = FrameCodec.DecodeLengthPrefix(buffer.AsSpan(0, FrameCodec.LengthPrefixSize));
        (MsgType decodedMsgType, uint correlationId, int payloadLength) = FrameCodec.DecodeFixedHeader(
            buffer.AsSpan(FrameCodec.LengthPrefixSize, FrameCodec.FixedHeaderSize), bodyLength);

        decodedMsgType.Should().Be(msgType);
        correlationId.Should().Be(7u);
        payloadLength.Should().Be(payload.Length);

        byte[] decodedPayload = buffer
            .AsSpan(FrameCodec.LengthPrefixSize + FrameCodec.FixedHeaderSize, payloadLength)
            .ToArray();
        decodedPayload.Should().Equal(payload);
    }

    [Fact]
    public void DecodeLengthPrefix_SmallerThanMinimumBody_Throws()
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 3); // smaller than FixedHeaderSize (9)

        Action act = () => FrameCodec.DecodeLengthPrefix(buffer);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DecodeFixedHeader_UnknownMsgType_Throws()
    {
        byte[] header = new byte[FrameCodec.FixedHeaderSize];
        header[0] = 0xFF; // not a defined MsgType
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), 0);

        Action act = () => FrameCodec.DecodeFixedHeader(header, expectedBodyLength: FrameCodec.FixedHeaderSize);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DecodeFixedHeader_PayloadLengthInconsistentWithBodyLength_Throws()
    {
        byte[] header = new byte[FrameCodec.FixedHeaderSize];
        header[0] = (byte)MsgType.Ping;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), 100); // claims 100 payload bytes

        // expectedBodyLength says there's no payload at all - inconsistent.
        Action act = () => FrameCodec.DecodeFixedHeader(header, expectedBodyLength: FrameCodec.FixedHeaderSize);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DecodeFixedHeader_NegativePayloadLength_Throws()
    {
        byte[] header = new byte[FrameCodec.FixedHeaderSize];
        header[0] = (byte)MsgType.Ping;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), -1);

        Action act = () => FrameCodec.DecodeFixedHeader(header, expectedBodyLength: FrameCodec.FixedHeaderSize - 1);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void EncodeFrame_DestinationTooSmall_Throws()
    {
        byte[] tooSmall = new byte[5];

        Action act = () => FrameCodec.EncodeFrame(tooSmall, MsgType.Ping, 1, new byte[10]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TotalSize_MatchesLengthPrefixPlusFixedHeaderPlusPayload()
    {
        FrameCodec.TotalSize(42).Should().Be(FrameCodec.LengthPrefixSize + FrameCodec.FixedHeaderSize + 42);
    }
}

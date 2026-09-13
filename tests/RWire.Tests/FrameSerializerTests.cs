using System.Buffers;
using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Pure unit tests for FrameSerializer - no R process involved, since
/// it operates on already-decoded Frame instances (Frame's internal
/// constructor is directly callable here, same assembly).
///
/// Frame.Dispose() returns its backing buffer to ArrayPool&lt;byte&gt;.Shared
/// whenever it's non-empty (see Frame.cs), so every Frame constructed
/// directly here rents its buffer from that same pool first - a plain
/// `new byte[n]` would get handed back to a pool it was never rented
/// from. Only a genuinely zero-length payload is safe to pass a plain
/// array for, since Dispose() skips the return in that case.
/// </summary>
public class FrameSerializerTests
{
    private static Frame MakeFrame(MsgType msgType, uint correlationId, byte[] payload)
    {
        byte[] buffer = payload.Length == 0 ? payload : ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer, 0);
        return new Frame(msgType, correlationId, buffer, payload.Length);
    }

    [Theory]
    [InlineData(MsgType.Ping, 0)]
    [InlineData(MsgType.Hello, 5)]
    [InlineData(MsgType.Result, 1000)]
    [InlineData(MsgType.Error, 1)]
    public void ToByteArray_ThenFromByteArray_RoundTripsExactly(MsgType msgType, int payloadSize)
    {
        byte[] payload = new byte[payloadSize];
        new Random(42).NextBytes(payload);

        using Frame original = MakeFrame(msgType, correlationId: 7, payload);

        byte[] bytes = FrameSerializer.ToByteArray(original);

        using Frame roundTripped = FrameSerializer.FromByteArray(bytes);

        roundTripped.MsgType.Should().Be(msgType);
        roundTripped.CorrelationId.Should().Be(7u);
        roundTripped.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void SaveToFile_ThenLoadFromFile_RoundTripsExactly()
    {
        byte[] payload = new byte[256];
        new Random(99).NextBytes(payload);
        using Frame original = MakeFrame(MsgType.Call, correlationId: 42, payload);

        string path = Path.Combine(Path.GetTempPath(), $"rwire-frame-{Guid.NewGuid():N}.bin");
        try
        {
            FrameSerializer.SaveToFile(original, path);

            using Frame loaded = FrameSerializer.LoadFromFile(path);

            loaded.MsgType.Should().Be(MsgType.Call);
            loaded.CorrelationId.Should().Be(42u);
            loaded.Payload.ToArray().Should().Equal(payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromByteArray_TruncatedData_ThrowsInvalidDataException()
    {
        using Frame original = MakeFrame(MsgType.Ping, correlationId: 1, new byte[16]);
        byte[] bytes = FrameSerializer.ToByteArray(original);
        byte[] truncated = bytes.AsSpan(0, bytes.Length - 1).ToArray();

        Action act = () => FrameSerializer.FromByteArray(truncated);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ToByteArray_NullFrame_Throws()
    {
        Action act = () => FrameSerializer.ToByteArray(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

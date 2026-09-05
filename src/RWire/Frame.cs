using System.Buffers;

namespace RWire;

/// <summary>
/// A fully decoded frame. The payload buffer is rented from
/// ArrayPool&lt;byte&gt;.Shared - callers must Dispose the frame (a
/// `using` statement is enough) to return it. Payload.Length is the
/// actual payload size; the rented backing array may be larger.
/// </summary>
public sealed class Frame : IDisposable
{
    private byte[]? _rentedBuffer;
    private bool _disposed;

    public MsgType MsgType { get; }
    public uint CorrelationId { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    internal Frame(MsgType msgType, uint correlationId, byte[] rentedBuffer, int payloadLength)
    {
        MsgType = msgType;
        CorrelationId = correlationId;
        _rentedBuffer = rentedBuffer;
        Payload = rentedBuffer.AsMemory(0, payloadLength);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Array.Empty<byte>() (used for zero-length payloads) is a
        // shared singleton, not a pool rental - never return it to the
        // pool, or a future Rent() elsewhere could hand out the same
        // instance for writing and corrupt unrelated code.
        if (_rentedBuffer is { Length: > 0 })
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
        }

        _rentedBuffer = null;
    }
}

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace RWire;

/// <summary>
/// Shared helpers for the simple [Len(4)][UTF-8 bytes] string encoding
/// used by several control-plane payloads (HELLO, ERROR messages, EVAL
/// expressions, CALL function names). Not used by RValueCodec's
/// character *vector* encoding, which has its own NA-aware length
/// sentinel (-1 = NA_character_) - see RValueCodec.
/// </summary>
internal static class WireStrings
{
    public static void Write(IBufferWriter<byte> writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);

        Span<byte> lengthSpan = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(lengthSpan, bytes.Length);
        writer.Advance(4);

        writer.Write(bytes);
    }

    public static string Read(ReadOnlySpan<byte> buffer, ref int offset)
    {
        if (offset + 4 > buffer.Length)
        {
            throw new InvalidDataException("Truncated while reading a string length.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;

        if (length < 0 || offset + length > buffer.Length)
        {
            throw new InvalidDataException("Truncated while reading string bytes.");
        }

        string value = Encoding.UTF8.GetString(buffer.Slice(offset, length));
        offset += length;
        return value;
    }
}

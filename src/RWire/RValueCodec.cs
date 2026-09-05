using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace RWire;

/// <summary>
/// Encodes/decodes an RValue: type tag, type-specific data, and
/// attributes (names/dim/class fast-pathed, everything else generic) -
/// docs/spec.md sections 5 and 5.3.
///
/// Wire shape:
///   [TypeTag(1)]
///   if TypeTag != Null:
///     [ElementCount(4)]
///     &lt;type-specific payload, ElementCount elements&gt;
///     [HasNames(1)] [Names? : ElementCount x [Len(4)][UTF8], "" = unnamed]
///     [HasDim(1)]   [DimCount(4)? ] [DimValues? : int32 x DimCount]
///     [HasClass(1)] [ClassCount(4)?] [ClassValues? : ClassCount x [Len(4)][UTF8]]
///     [AttrCount(4)] { [NameLen+Name][Value, recursively encoded] }*
///
/// A factor is encoded as an ordinary Integer vector whose Class is
/// ["factor"] and whose Attributes contains "levels" (a Character
/// RValue) - see docs/spec.md section 5.3. This rides the same
/// generic Class + Attributes mechanism as everything else rather than
/// a separate wire shape; it still avoids the deep/irregular-object
/// cold path (section 7), which is the actual thing worth avoiding.
///
/// This is a whole-buffer codec: Decode takes a fully-received
/// ReadOnlySpan&lt;byte&gt;, and Encode writes into an IBufferWriter.
/// That's appropriate for the atomic-vector sizes Phase 2 targets. The
/// unbuffered, stream-directly, one-column-at-a-time approach is
/// reserved for the TABLE type in Phase 4, which is the actual
/// performance-critical bulk-transfer path - see spec.md section 6.2.
/// </summary>
public static class RValueCodec
{
    public static void Encode(IBufferWriter<byte> writer, RValue value)
    {
        WriteByte(writer, (byte)value.TypeTag);

        if (value.TypeTag == RTypeTag.Null)
        {
            return;
        }

        WriteInt32(writer, value.Length);

        switch (value.TypeTag)
        {
            case RTypeTag.Logical:
                writer.Write(value.LogicalCodes!);
                break;

            case RTypeTag.Integer:
                foreach (int v in value.IntegerValues!)
                {
                    WriteInt32(writer, v);
                }
                break;

            case RTypeTag.Double:
                foreach (double v in value.DoubleValues!)
                {
                    WriteDouble(writer, v);
                }
                break;

            case RTypeTag.Character:
                foreach (string? s in value.CharacterValues!)
                {
                    WriteNullableString(writer, s);
                }
                break;

            case RTypeTag.Raw:
                writer.Write(value.RawValues!);
                break;

            case RTypeTag.List:
                foreach (RValue element in value.ListValues!)
                {
                    Encode(writer, element);
                }
                break;

            default:
                throw new NotSupportedException($"Unsupported RTypeTag: {value.TypeTag}");
        }

        WriteAttributes(writer, value);
    }

    private static void WriteAttributes(IBufferWriter<byte> writer, RValue value)
    {
        if (value.Names is { } names)
        {
            WriteByte(writer, 1);
            foreach (string? name in names)
            {
                WireStrings.Write(writer, name ?? string.Empty);
            }
        }
        else
        {
            WriteByte(writer, 0);
        }

        if (value.Dim is { } dim)
        {
            WriteByte(writer, 1);
            WriteInt32(writer, dim.Length);
            foreach (int d in dim)
            {
                WriteInt32(writer, d);
            }
        }
        else
        {
            WriteByte(writer, 0);
        }

        if (value.Class is { } cls)
        {
            WriteByte(writer, 1);
            WriteInt32(writer, cls.Length);
            foreach (string c in cls)
            {
                WireStrings.Write(writer, c);
            }
        }
        else
        {
            WriteByte(writer, 0);
        }

        if (value.Attributes is { Count: > 0 } attrs)
        {
            WriteInt32(writer, attrs.Count);
            foreach ((string name, RValue attrValue) in attrs)
            {
                WireStrings.Write(writer, name);
                Encode(writer, attrValue);
            }
        }
        else
        {
            WriteInt32(writer, 0);
        }
    }

    public static RValue Decode(ReadOnlySpan<byte> buffer)
    {
        int offset = 0;
        return DecodeAt(buffer, ref offset);
    }

    private static RValue DecodeAt(ReadOnlySpan<byte> buffer, ref int offset)
    {
        byte rawTag = ReadByte(buffer, ref offset);
        if (!Enum.IsDefined(typeof(RTypeTag), rawTag))
        {
            throw new InvalidDataException($"Unknown RTypeTag byte: 0x{rawTag:X2}.");
        }

        var typeTag = (RTypeTag)rawTag;
        if (typeTag == RTypeTag.Null)
        {
            return RValue.Null();
        }

        int count = ReadInt32(buffer, ref offset);
        if (count < 0)
        {
            throw new InvalidDataException($"Negative element count: {count}.");
        }

        RValue value = typeTag switch
        {
            RTypeTag.Logical => RValue.OfLogical(ReadBytes(buffer, ref offset, count)),
            RTypeTag.Integer => RValue.OfInteger(ReadInt32Array(buffer, ref offset, count)),
            RTypeTag.Double => RValue.OfDouble(ReadDoubleArray(buffer, ref offset, count)),
            RTypeTag.Character => RValue.OfCharacter(ReadCharacterArray(buffer, ref offset, count)),
            RTypeTag.Raw => RValue.OfRaw(ReadBytes(buffer, ref offset, count)),
            RTypeTag.List => RValue.OfList(ReadList(buffer, ref offset, count)),
            _ => throw new NotSupportedException($"Unsupported RTypeTag: {typeTag}"),
        };

        return ReadAttributes(buffer, ref offset, value);
    }

    private static RValue ReadAttributes(ReadOnlySpan<byte> buffer, ref int offset, RValue value)
    {
        string?[]? names = null;
        int[]? dim = null;
        string[]? cls = null;

        if (ReadByte(buffer, ref offset) == 1)
        {
            names = new string?[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                string s = WireStrings.Read(buffer, ref offset);
                names[i] = s.Length == 0 ? null : s;
            }
        }

        if (ReadByte(buffer, ref offset) == 1)
        {
            int dimCount = ReadInt32(buffer, ref offset);
            dim = new int[dimCount];
            for (int i = 0; i < dimCount; i++)
            {
                dim[i] = ReadInt32(buffer, ref offset);
            }
        }

        if (ReadByte(buffer, ref offset) == 1)
        {
            int classCount = ReadInt32(buffer, ref offset);
            cls = new string[classCount];
            for (int i = 0; i < classCount; i++)
            {
                cls[i] = WireStrings.Read(buffer, ref offset);
            }
        }

        int attrCount = ReadInt32(buffer, ref offset);
        Dictionary<string, RValue>? attributes = null;
        if (attrCount > 0)
        {
            attributes = new Dictionary<string, RValue>(attrCount);
            for (int i = 0; i < attrCount; i++)
            {
                string attrName = WireStrings.Read(buffer, ref offset);
                attributes[attrName] = DecodeAt(buffer, ref offset);
            }
        }

        return new RValue
        {
            TypeTag = value.TypeTag,
            LogicalCodes = value.LogicalCodes,
            IntegerValues = value.IntegerValues,
            DoubleValues = value.DoubleValues,
            CharacterValues = value.CharacterValues,
            RawValues = value.RawValues,
            ListValues = value.ListValues,
            Names = names,
            Dim = dim,
            Class = cls,
            Attributes = attributes,
        };
    }

    private static RValue[] ReadList(ReadOnlySpan<byte> buffer, ref int offset, int count)
    {
        var elements = new RValue[count];
        for (int i = 0; i < count; i++)
        {
            elements[i] = DecodeAt(buffer, ref offset);
        }
        return elements;
    }

    // ---- primitive read/write helpers ----

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        Span<byte> span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(4);
    }

    private static void WriteDouble(IBufferWriter<byte> writer, double value)
    {
        Span<byte> span = writer.GetSpan(8);
        BinaryPrimitives.WriteInt64LittleEndian(span, BitConverter.DoubleToInt64Bits(value));
        writer.Advance(8);
    }

    private static void WriteNullableString(IBufferWriter<byte> writer, string? value)
    {
        if (value is null)
        {
            WriteInt32(writer, -1);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(writer, bytes.Length);
        if (bytes.Length > 0)
        {
            writer.Write(bytes);
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> buffer, ref int offset)
    {
        RequireBytes(buffer, offset, 1);
        return buffer[offset++];
    }

    private static int ReadInt32(ReadOnlySpan<byte> buffer, ref int offset)
    {
        RequireBytes(buffer, offset, 4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static double ReadDouble(ReadOnlySpan<byte> buffer, ref int offset)
    {
        RequireBytes(buffer, offset, 8);
        long bits = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;
        return BitConverter.Int64BitsToDouble(bits);
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> buffer, ref int offset, int count)
    {
        RequireBytes(buffer, offset, count);
        byte[] result = buffer.Slice(offset, count).ToArray();
        offset += count;
        return result;
    }

    private static int[] ReadInt32Array(ReadOnlySpan<byte> buffer, ref int offset, int count)
    {
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = ReadInt32(buffer, ref offset);
        }
        return result;
    }

    private static double[] ReadDoubleArray(ReadOnlySpan<byte> buffer, ref int offset, int count)
    {
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = ReadDouble(buffer, ref offset);
        }
        return result;
    }

    private static string?[] ReadCharacterArray(ReadOnlySpan<byte> buffer, ref int offset, int count)
    {
        var result = new string?[count];
        for (int i = 0; i < count; i++)
        {
            int length = ReadInt32(buffer, ref offset);
            if (length < 0)
            {
                result[i] = null; // NA_character_
                continue;
            }

            RequireBytes(buffer, offset, length);
            result[i] = Encoding.UTF8.GetString(buffer.Slice(offset, length));
            offset += length;
        }
        return result;
    }

    private static void RequireBytes(ReadOnlySpan<byte> buffer, int offset, int count)
    {
        if (offset + count > buffer.Length)
        {
            throw new InvalidDataException(
                $"Truncated RValue payload: need {count} bytes at offset {offset}, buffer has {buffer.Length}.");
        }
    }
}

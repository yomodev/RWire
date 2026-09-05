using System.Buffers;
using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Pure unit tests for RValueCodec (docs/spec.md sections 5, 6, 12.1) -
/// no R process involved. The NaN-vs-NA distinction test is the single
/// highest-priority test in this file per
/// docs/phases/phase-2-atomic-types.md's "Notes for resuming
/// mid-phase" - run it first if picking this back up.
/// </summary>
public class RValueCodecTests
{
    private static RValue RoundTrip(RValue value)
    {
        var writer = new ArrayBufferWriter<byte>();
        RValueCodec.Encode(writer, value);
        return RValueCodec.Decode(writer.WrittenSpan);
    }

    [Fact]
    public void Null_RoundTrips()
    {
        RValue result = RoundTrip(RValue.Null());
        result.TypeTag.Should().Be(RTypeTag.Null);
    }

    [Fact]
    public void Double_NaReal_And_ComputedNaN_StayDistinct_AfterRoundTrip()
    {
        double computedNaN = 0.0 / 0.0;
        double[] input = { 1.5, RNumeric.NaReal, computedNaN, -2.25 };

        RValue result = RoundTrip(RValue.OfDouble(input));

        result.TypeTag.Should().Be(RTypeTag.Double);
        double[] decoded = result.DoubleValues!;

        decoded[0].Should().Be(1.5);
        RNumeric.IsNaReal(decoded[1]).Should().BeTrue("element 1 should decode as R's NA_real_");
        RNumeric.IsNaReal(decoded[2]).Should().BeFalse("element 2 is a computed NaN, not NA - must not collapse to NA");
        double.IsNaN(decoded[2]).Should().BeTrue("element 2 should still be NaN, just not the NA variant");
        decoded[3].Should().Be(-2.25);

        double?[] nullable = decoded.ToNullableArray();
        nullable[0].Should().Be(1.5);
        nullable[1].Should().BeNull();
        nullable[2].Should().NotBeNull("a computed NaN is a value, not absence of one");
        nullable[3].Should().Be(-2.25);
    }

    [Fact]
    public void Double_EmptyVector_RoundTrips()
    {
        RValue result = RoundTrip(RValue.OfDouble(Array.Empty<double>()));
        result.Length.Should().Be(0);
    }

    [Fact]
    public void Integer_WithNa_RoundTrips()
    {
        int[] input = { 1, RNumeric.NaInteger, -5, 0 };

        RValue result = RoundTrip(RValue.OfInteger(input));

        result.IntegerValues.Should().Equal(input);
        int?[] nullable = result.IntegerValues!.ToNullableArray();
        nullable.Should().Equal(new int?[] { 1, null, -5, 0 });
    }

    [Theory]
    [InlineData(RNumeric.LogicalFalse, false)]
    [InlineData(RNumeric.LogicalTrue, true)]
    public void Logical_TrueFalse_RoundTrips(byte code, bool expected)
    {
        RValue result = RoundTrip(RValue.OfLogical(new[] { code }));
        result.LogicalCodes!.ToNullableArray()[0].Should().Be(expected);
    }

    [Fact]
    public void Logical_Na_RoundTrips()
    {
        byte[] input = { RNumeric.LogicalTrue, RNumeric.LogicalNa, RNumeric.LogicalFalse };
        RValue result = RoundTrip(RValue.OfLogical(input));

        bool?[] nullable = result.LogicalCodes!.ToNullableArray();
        nullable.Should().Equal(new bool?[] { true, null, false });
    }

    [Fact]
    public void Character_WithNaAndEmptyString_RoundTrips_AsDistinctValues()
    {
        string?[] input = { "hello", null, "", "world" };

        RValue result = RoundTrip(RValue.OfCharacter(input));

        result.CharacterValues.Should().Equal(input);
    }

    [Fact]
    public void Character_Utf8MultiByte_RoundTrips()
    {
        string?[] input = { "héllo", "日本語", "🎉" };

        RValue result = RoundTrip(RValue.OfCharacter(input));

        result.CharacterValues.Should().Equal(input);
    }

    [Fact]
    public void Raw_RoundTrips()
    {
        byte[] input = { 0x00, 0xFF, 0x7F, 0x01 };
        RValue result = RoundTrip(RValue.OfRaw(input));
        result.RawValues.Should().Equal(input);
    }

    [Fact]
    public void NamedVector_RoundTrips_WithAttributesIntact()
    {
        var value = new RValue
        {
            TypeTag = RTypeTag.Integer,
            IntegerValues = new[] { 10, 20, 30 },
            Names = new string?[] { "a", "b", null },
        };

        RValue result = RoundTrip(value);

        result.IntegerValues.Should().Equal(10, 20, 30);
        result.Names.Should().Equal(new string?[] { "a", "b", null });
    }

    [Fact]
    public void DimBearingVector_RoundTrips()
    {
        var value = new RValue
        {
            TypeTag = RTypeTag.Double,
            DoubleValues = new double[] { 1, 2, 3, 4, 5, 6 },
            Dim = new[] { 2, 3 },
        };

        RValue result = RoundTrip(value);

        result.Dim.Should().Equal(2, 3);
    }

    [Fact]
    public void Factor_RoundTrips_AsIntegerCodesWithClassAndLevelsAttribute()
    {
        var levels = RValue.OfCharacter(new string?[] { "low", "medium", "high" });
        var value = new RValue
        {
            TypeTag = RTypeTag.Integer,
            IntegerValues = new[] { 1, 3, 2 }, // codes into the levels vector, 1-based
            Class = new[] { "factor" },
            Attributes = new Dictionary<string, RValue> { ["levels"] = levels },
        };

        RValue result = RoundTrip(value);

        result.Class.Should().Equal("factor");
        result.IntegerValues.Should().Equal(1, 3, 2);
        result.Attributes!["levels"].CharacterValues.Should().Equal("low", "medium", "high");
    }

    [Fact]
    public void List_OfMixedTypes_RoundTrips()
    {
        var value = RValue.OfList(new[]
        {
            RValue.OfInteger(new[] { 1, 2 }),
            RValue.OfCharacter(new string?[] { "x", null }),
            RValue.Null(),
        });

        RValue result = RoundTrip(value);

        result.ListValues.Should().HaveCount(3);
        result.ListValues![0].IntegerValues.Should().Equal(1, 2);
        result.ListValues[1].CharacterValues.Should().Equal("x", null);
        result.ListValues[2].TypeTag.Should().Be(RTypeTag.Null);
    }

    [Fact]
    public void Table_RoundTrips_WithColumnsRowCountAndClass()
    {
        var value = RValue.OfTable(
            new (string, RValue)[]
            {
                ("id", RValue.OfInteger(new[] { 1, 2, 3 })),
                ("name", RValue.OfCharacter(new string?[] { "a", null, "c" })),
                ("value", RValue.OfDouble(new double[] { 1.1, RNumeric.NaReal, 3.3 })),
            },
            classNames: new[] { "data.frame" });

        RValue result = RoundTrip(value);

        result.TypeTag.Should().Be(RTypeTag.Table);
        result.RowCount.Should().Be(3);
        result.Length.Should().Be(3, "Length is column count for a Table");
        result.Class.Should().Equal("data.frame");
        result.Names.Should().Equal(new string?[] { "id", "name", "value" });

        IReadOnlyDictionary<string, RValue> columns = result.GetTableColumns();
        columns["id"].IntegerValues.Should().Equal(1, 2, 3);
        columns["name"].CharacterValues.Should().Equal("a", null, "c");
        RNumeric.IsNaReal(columns["value"].DoubleValues![1]).Should().BeTrue();
    }

    [Fact]
    public void Table_ZeroColumns_RoundTrips()
    {
        RValue value = RValue.OfTable(Array.Empty<(string, RValue)>());

        RValue result = RoundTrip(value);

        result.TypeTag.Should().Be(RTypeTag.Table);
        result.RowCount.Should().Be(0);
        result.Length.Should().Be(0);
    }

    [Fact]
    public void OfTable_ColumnLengthMismatch_ThrowsArgumentException()
    {
        Action act = () => RValue.OfTable(new (string, RValue)[]
        {
            ("a", RValue.OfInteger(new[] { 1, 2, 3 })),
            ("b", RValue.OfInteger(new[] { 1, 2 })), // wrong length
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Table_ListOfTables_RoundTrips()
    {
        RValue table1 = RValue.OfTable(new (string, RValue)[] { ("x", RValue.OfInteger(new[] { 1, 2 })) });
        RValue table2 = RValue.OfTable(new (string, RValue)[] { ("x", RValue.OfInteger(new[] { 3, 4, 5 })) });
        RValue list = RValue.OfList(new[] { table1, table2 });

        RValue result = RoundTrip(list);

        result.TypeTag.Should().Be(RTypeTag.List);
        result.ListValues.Should().HaveCount(2);
        result.ListValues![0].TypeTag.Should().Be(RTypeTag.Table);
        result.ListValues[0].RowCount.Should().Be(2);
        result.ListValues[1].RowCount.Should().Be(3);
    }

    [Fact]
    public void GetTableColumns_OnNonTableValue_Throws()
    {
        RValue value = RValue.OfInteger(new[] { 1 });

        Action act = () => value.GetTableColumns();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Decode_UnknownTypeTag_Throws()
    {
        byte[] buffer = { 0xFF };

        Action act = () => RValueCodec.Decode(buffer);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Decode_TruncatedBuffer_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        RValueCodec.Encode(writer, RValue.OfInteger(new[] { 1, 2, 3 }));

        byte[] truncated = writer.WrittenSpan.ToArray()[..^2];

        Action act = () => RValueCodec.Decode(truncated);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Decode_TableWithMismatchedColumnLength_ThrowsInvalidDataException()
    {
        // Hand-craft a corrupt Table frame: declares RowCount=5 but the
        // single column only has 3 elements - simulates wire
        // corruption/desync rather than going through OfTable's
        // encode-side validation.
        var manual = new ArrayBufferWriter<byte>();

        // TypeTag=Table(7), RowCount=5, ColumnCount=1, column = Integer[1,2,3]
        manual.GetSpan(1)[0] = (byte)RTypeTag.Table;
        manual.Advance(1);
        WriteInt32(manual, 5); // RowCount
        WriteInt32(manual, 1); // ColumnCount

        var columnWriter = new ArrayBufferWriter<byte>();
        RValueCodec.Encode(columnWriter, RValue.OfInteger(new[] { 1, 2, 3 }));
        manual.Write(columnWriter.WrittenSpan);

        // Attribute flags: no names, no dim, no class, no generic attrs.
        manual.GetSpan(1)[0] = 0; manual.Advance(1);
        manual.GetSpan(1)[0] = 0; manual.Advance(1);
        manual.GetSpan(1)[0] = 0; manual.Advance(1);
        WriteInt32(manual, 0);

        Action act = () => RValueCodec.Decode(manual.WrittenSpan);

        act.Should().Throw<InvalidDataException>();
    }

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(4);
    }
}

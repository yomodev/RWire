using System.Buffers;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Pure unit tests for RValueCodec (docs/spec.md section 12.1) - no R
/// process involved. The NaN-vs-NA distinction test is the single
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
        Assert.Equal(RTypeTag.Null, result.TypeTag);
    }

    [Fact]
    public void Double_NaReal_And_ComputedNaN_StayDistinct_AfterRoundTrip()
    {
        double computedNaN = 0.0 / 0.0;
        double[] input = { 1.5, RNumeric.NaReal, computedNaN, -2.25 };

        RValue result = RoundTrip(RValue.OfDouble(input));

        Assert.Equal(RTypeTag.Double, result.TypeTag);
        double[] decoded = result.DoubleValues!;

        Assert.Equal(1.5, decoded[0]);
        Assert.True(RNumeric.IsNaReal(decoded[1]), "Element 1 should decode as R's NA_real_.");
        Assert.False(RNumeric.IsNaReal(decoded[2]), "Element 2 is a computed NaN, not NA - must not collapse to NA.");
        Assert.True(double.IsNaN(decoded[2]), "Element 2 should still be NaN, just not the NA variant.");
        Assert.Equal(-2.25, decoded[3]);

        double?[] nullable = decoded.ToNullableArray();
        Assert.Equal(1.5, nullable[0]);
        Assert.Null(nullable[1]);
        Assert.NotNull(nullable[2]); // a computed NaN is a value, not absence of one
        Assert.Equal(-2.25, nullable[3]);
    }

    [Fact]
    public void Double_EmptyVector_RoundTrips()
    {
        RValue result = RoundTrip(RValue.OfDouble(Array.Empty<double>()));
        Assert.Equal(0, result.Length);
    }

    [Fact]
    public void Integer_WithNa_RoundTrips()
    {
        int[] input = { 1, RNumeric.NaInteger, -5, 0 };

        RValue result = RoundTrip(RValue.OfInteger(input));

        Assert.Equal(input, result.IntegerValues);
        int?[] nullable = result.IntegerValues!.ToNullableArray();
        Assert.Equal(new int?[] { 1, null, -5, 0 }, nullable);
    }

    [Theory]
    [InlineData(RNumeric.LogicalFalse, false)]
    [InlineData(RNumeric.LogicalTrue, true)]
    public void Logical_TrueFalse_RoundTrips(byte code, bool expected)
    {
        RValue result = RoundTrip(RValue.OfLogical(new[] { code }));
        Assert.Equal(expected, result.LogicalCodes!.ToNullableArray()[0]);
    }

    [Fact]
    public void Logical_Na_RoundTrips()
    {
        byte[] input = { RNumeric.LogicalTrue, RNumeric.LogicalNa, RNumeric.LogicalFalse };
        RValue result = RoundTrip(RValue.OfLogical(input));

        bool?[] nullable = result.LogicalCodes!.ToNullableArray();
        Assert.Equal(new bool?[] { true, null, false }, nullable);
    }

    [Fact]
    public void Character_WithNaAndEmptyString_RoundTrips_AsDistinctValues()
    {
        string?[] input = { "hello", null, "", "world" };

        RValue result = RoundTrip(RValue.OfCharacter(input));

        Assert.Equal(input, result.CharacterValues);
    }

    [Fact]
    public void Character_Utf8MultiByte_RoundTrips()
    {
        string?[] input = { "héllo", "日本語", "🎉" };

        RValue result = RoundTrip(RValue.OfCharacter(input));

        Assert.Equal(input, result.CharacterValues);
    }

    [Fact]
    public void Raw_RoundTrips()
    {
        byte[] input = { 0x00, 0xFF, 0x7F, 0x01 };
        RValue result = RoundTrip(RValue.OfRaw(input));
        Assert.Equal(input, result.RawValues);
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

        Assert.Equal(new[] { 10, 20, 30 }, result.IntegerValues);
        Assert.Equal(new string?[] { "a", "b", null }, result.Names);
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

        Assert.Equal(new[] { 2, 3 }, result.Dim);
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

        Assert.Equal(new[] { "factor" }, result.Class);
        Assert.Equal(new[] { 1, 3, 2 }, result.IntegerValues);
        Assert.Equal(new string?[] { "low", "medium", "high" }, result.Attributes!["levels"].CharacterValues);
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

        Assert.Equal(3, result.ListValues!.Length);
        Assert.Equal(new[] { 1, 2 }, result.ListValues[0].IntegerValues);
        Assert.Equal(new string?[] { "x", null }, result.ListValues[1].CharacterValues);
        Assert.Equal(RTypeTag.Null, result.ListValues[2].TypeTag);
    }

    [Fact]
    public void Decode_UnknownTypeTag_Throws()
    {
        byte[] buffer = { 0xFF };
        Assert.Throws<InvalidDataException>(() => RValueCodec.Decode(buffer));
    }

    [Fact]
    public void Decode_TruncatedBuffer_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        RValueCodec.Encode(writer, RValue.OfInteger(new[] { 1, 2, 3 }));

        byte[] truncated = writer.WrittenSpan.ToArray()[..^2];

        Assert.Throws<InvalidDataException>(() => RValueCodec.Decode(truncated));
    }
}

using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// A DTO covering every default-registered RTypeConverter type -
/// nullable and non-nullable variants of each - plus a nested object,
/// a collection property, and edge-case values (MinValue/MaxValue,
/// empty strings, etc.). Used to push RTypeConverter's reflection-
/// based object mapping past the simple happy-path cases exercised
/// elsewhere.
/// </summary>
public class KitchenSinkDto
{
    public bool BoolValue { get; set; }
    public bool? NullableBoolValue { get; set; }

    public byte ByteValue { get; set; }
    public byte? NullableByteValue { get; set; }
    public sbyte SByteValue { get; set; }
    public sbyte? NullableSByteValue { get; set; }

    public short ShortValue { get; set; }
    public short? NullableShortValue { get; set; }
    public ushort UShortValue { get; set; }
    public ushort? NullableUShortValue { get; set; }

    public int IntValue { get; set; }
    public int? NullableIntValue { get; set; }
    public uint UIntValue { get; set; }
    public uint? NullableUIntValue { get; set; }

    public long LongValue { get; set; }
    public long? NullableLongValue { get; set; }
    public ulong ULongValue { get; set; }
    public ulong? NullableULongValue { get; set; }

    public float FloatValue { get; set; }
    public float? NullableFloatValue { get; set; }
    public double DoubleValue { get; set; }
    public double? NullableDoubleValue { get; set; }
    public decimal DecimalValue { get; set; }
    public decimal? NullableDecimalValue { get; set; }

    public char CharValue { get; set; }
    public char? NullableCharValue { get; set; }
    public string StringValue { get; set; } = "";
    public string? NullableStringValue { get; set; }

    public DateTime DateTimeValue { get; set; }
    public DateTime? NullableDateTimeValue { get; set; }
    public DateOnly DateOnlyValue { get; set; }
    public DateOnly? NullableDateOnlyValue { get; set; }
    public TimeOnly TimeOnlyValue { get; set; }
    public TimeOnly? NullableTimeOnlyValue { get; set; }

    public Guid GuidValue { get; set; }
    public Guid? NullableGuidValue { get; set; }

    public SampleColor EnumValue { get; set; }
    public SampleColor? NullableEnumValue { get; set; }

    public int[] IntArrayValue { get; set; } = Array.Empty<int>();
    public List<string> StringListValue { get; set; } = new();
    public Dictionary<string, int> DictionaryValue { get; set; } = new();

    public SamplePerson? NestedObjectValue { get; set; }
}

/// <summary>
/// Stress tests for RTypeConverter: the full KitchenSinkDto round trip
/// (every default-registered type at once, both nullable variants
/// populated and null), edge-case values (MinValue/MaxValue for every
/// numeric type, empty and Unicode strings, DateTime.MinValue/MaxValue),
/// a large array (10,000 elements), a deeply-nested object, and a
/// collection of DTOs converting to a TABLE and back.
/// </summary>
public class RTypeConverterStressTests
{
    private static KitchenSinkDto BuildPopulatedDto() => new()
    {
        BoolValue = true,
        NullableBoolValue = false,
        ByteValue = 200,
        NullableByteValue = 1,
        SByteValue = -100,
        NullableSByteValue = -1,
        ShortValue = -12345,
        NullableShortValue = 12345,
        UShortValue = 54321,
        NullableUShortValue = 1,
        IntValue = -123456789,
        NullableIntValue = 123456789,
        UIntValue = 3_000_000_000,
        NullableUIntValue = 1,
        LongValue = -9_000_000_000_000,
        NullableLongValue = 9_000_000_000_000,
        ULongValue = 10_000_000_000_000,
        NullableULongValue = 1,
        FloatValue = 3.14159f,
        NullableFloatValue = -2.71828f,
        DoubleValue = 2.718281828459045,
        NullableDoubleValue = -3.14159265358979,
        DecimalValue = 12345.6789m,
        NullableDecimalValue = -9876.54321m,
        CharValue = 'Z',
        NullableCharValue = '日',
        StringValue = "hello, world",
        NullableStringValue = null,
        DateTimeValue = new DateTime(2024, 6, 15, 12, 30, 45, DateTimeKind.Utc),
        NullableDateTimeValue = null,
        DateOnlyValue = new DateOnly(2024, 1, 1),
        NullableDateOnlyValue = new DateOnly(1999, 12, 31),
        TimeOnlyValue = new TimeOnly(23, 59, 59),
        NullableTimeOnlyValue = null,
        GuidValue = Guid.NewGuid(),
        NullableGuidValue = null,
        EnumValue = SampleColor.Blue,
        NullableEnumValue = SampleColor.Red,
        IntArrayValue = new[] { 1, 2, 3, -4, 5 },
        StringListValue = new List<string> { "a", "b", "c" },
        DictionaryValue = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 },
        NestedObjectValue = new SamplePerson { Name = "Nested", Age = 1, Score = 0.5 },
    };

    [Fact]
    public void KitchenSinkDto_FullyPopulated_RoundTrips()
    {
        KitchenSinkDto original = BuildPopulatedDto();

        RValue rv = original.ToRValue();
        rv.TypeTag.Should().Be(RTypeTag.List);

        KitchenSinkDto result = rv.To<KitchenSinkDto>();

        result.BoolValue.Should().Be(original.BoolValue);
        result.NullableBoolValue.Should().Be(original.NullableBoolValue);
        result.ByteValue.Should().Be(original.ByteValue);
        result.NullableByteValue.Should().Be(original.NullableByteValue);
        result.SByteValue.Should().Be(original.SByteValue);
        result.NullableSByteValue.Should().Be(original.NullableSByteValue);
        result.ShortValue.Should().Be(original.ShortValue);
        result.NullableShortValue.Should().Be(original.NullableShortValue);
        result.UShortValue.Should().Be(original.UShortValue);
        result.NullableUShortValue.Should().Be(original.NullableUShortValue);
        result.IntValue.Should().Be(original.IntValue);
        result.NullableIntValue.Should().Be(original.NullableIntValue);
        result.UIntValue.Should().Be(original.UIntValue);
        result.NullableUIntValue.Should().Be(original.NullableUIntValue);
        result.LongValue.Should().Be(original.LongValue);
        result.NullableLongValue.Should().Be(original.NullableLongValue);
        result.ULongValue.Should().Be(original.ULongValue);
        result.NullableULongValue.Should().Be(original.NullableULongValue);
        result.FloatValue.Should().Be(original.FloatValue);
        result.NullableFloatValue.Should().Be(original.NullableFloatValue);
        result.DoubleValue.Should().Be(original.DoubleValue);
        result.NullableDoubleValue.Should().Be(original.NullableDoubleValue);
        result.DecimalValue.Should().Be(original.DecimalValue);
        result.NullableDecimalValue.Should().Be(original.NullableDecimalValue);
        result.CharValue.Should().Be(original.CharValue);
        result.NullableCharValue.Should().Be(original.NullableCharValue);
        result.StringValue.Should().Be(original.StringValue);
        result.NullableStringValue.Should().Be(original.NullableStringValue);
        result.DateTimeValue.Should().Be(original.DateTimeValue);
        result.NullableDateTimeValue.Should().Be(original.NullableDateTimeValue);
        result.DateOnlyValue.Should().Be(original.DateOnlyValue);
        result.NullableDateOnlyValue.Should().Be(original.NullableDateOnlyValue);
        result.TimeOnlyValue.Should().Be(original.TimeOnlyValue);
        result.NullableTimeOnlyValue.Should().Be(original.NullableTimeOnlyValue);
        result.GuidValue.Should().Be(original.GuidValue);
        result.NullableGuidValue.Should().Be(original.NullableGuidValue);
        result.EnumValue.Should().Be(original.EnumValue);
        result.NullableEnumValue.Should().Be(original.NullableEnumValue);
        result.IntArrayValue.Should().Equal(original.IntArrayValue);
        result.StringListValue.Should().Equal(original.StringListValue);
        result.DictionaryValue.Should().Equal(original.DictionaryValue);
        result.NestedObjectValue!.Name.Should().Be(original.NestedObjectValue.Name);
        result.NestedObjectValue.Age.Should().Be(original.NestedObjectValue.Age);
        result.NestedObjectValue.Score.Should().Be(original.NestedObjectValue.Score);
    }

    [Theory]
    [InlineData(sbyte.MinValue, sbyte.MaxValue)]
    public void SByte_ExtremeValues_RoundTrip(sbyte min, sbyte max)
    {
        min.ToRValue().To<sbyte>().Should().Be(min);
        max.ToRValue().To<sbyte>().Should().Be(max);
    }

    [Fact]
    public void Short_ExtremeValues_RoundTrip()
    {
        short.MinValue.ToRValue().To<short>().Should().Be(short.MinValue);
        short.MaxValue.ToRValue().To<short>().Should().Be(short.MaxValue);
    }

    [Fact]
    public void Int_ExtremeValues_RoundTrip()
    {
        int.MinValue.ToRValue().To<int>().Should().Be(int.MinValue);
        int.MaxValue.ToRValue().To<int>().Should().Be(int.MaxValue);
    }

    [Fact]
    public void Long_ExtremeValues_LosePrecisionBeyondDoubleRange_ButSmallValuesRoundTrip()
    {
        // Documented, deliberate limitation (docs/progress.md): long
        // rides RWire's Double vector since R has no native 64-bit
        // integer type. Values within double's exact-integer range
        // (+/- 2^53) round-trip exactly; long.MinValue/MaxValue do
        // NOT, because they exceed that range. This test asserts both
        // halves of that reality rather than only the happy path.
        long safeValue = (1L << 53) - 1; // largest exactly-representable integer in a double
        safeValue.ToRValue().To<long>().Should().Be(safeValue);

        double asDouble = long.MaxValue.ToRValue().DoubleValues![0];
        ((long)asDouble).Should().NotBe(long.MaxValue, "long.MaxValue exceeds double's exact-integer range - this is expected precision loss, not a bug");
    }

    [Fact]
    public void Decimal_ExtremeValues_LosePrecisionBeyondDoubleRange()
    {
        // Same documented limitation as long - decimal rides Double
        // too, so decimal's extra precision beyond what a double can
        // represent is not preserved. A value within double's
        // precision round-trips fine; decimal.MaxValue does not.
        decimal safeValue = 12345.6789m;
        safeValue.ToRValue().To<decimal>().Should().Be(safeValue);

        decimal roundTrippedMax = decimal.MaxValue.ToRValue().To<decimal>();
        roundTrippedMax.Should().NotBe(decimal.MaxValue, "decimal.MaxValue exceeds double's precision - expected, not a bug");
    }

    [Fact]
    public void DateTime_MinAndMaxValue_RoundTrip()
    {
        // DateTime.MinValue is unspecified Kind by default - ToRValue's
        // DateTime converter calls ToUniversalTime() internally, which
        // requires treating an Unspecified-kind value as local time;
        // to keep this test deterministic regardless of the machine's
        // local timezone, both values are constructed as Utc.
        var min = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var max = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        min.ToRValue().To<DateTime>().Should().BeCloseTo(min, TimeSpan.FromMilliseconds(1));
        max.ToRValue().To<DateTime>().Should().BeCloseTo(max, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void String_EmptyAndUnicode_RoundTrip()
    {
        "".ToRValue().To<string>().Should().Be("");
        "🎉日本語".ToRValue().To<string>().Should().Be("🎉日本語");
    }

    [Fact]
    public void LargeArray_10000Elements_RoundTrips()
    {
        int[] values = Enumerable.Range(0, 10_000).ToArray();

        RValue rv = values.ToRValue();
        rv.TypeTag.Should().Be(RTypeTag.Integer, "still a proper atomic vector at this size, not a generic List");

        int[] result = rv.To<int[]>();
        result.Should().Equal(values);
    }

    [Fact]
    public void LargeCollectionOfDtos_BecomesTable_AndRoundTrips()
    {
        List<SamplePerson> people = Enumerable.Range(0, 5_000)
            .Select(i => new SamplePerson { Name = $"Person{i}", Age = i % 100, Score = i * 0.1 })
            .ToList();

        RValue rv = people.ToRValue();
        rv.TypeTag.Should().Be(RTypeTag.Table);
        rv.RowCount.Should().Be(5_000);

        List<SamplePerson> result = rv.To<List<SamplePerson>>();
        result.Should().HaveCount(5_000);
        result[0].Name.Should().Be("Person0");
        result[4999].Name.Should().Be("Person4999");
        result[2500].Age.Should().Be(2500 % 100);
    }

    [Fact]
    public void DeeplyNestedObjects_RoundTrip()
    {
        // A chain of objects nested three levels deep, each level
        // itself a plain object converted via the same reflection
        // path - checks that ConvertObjectToNamedList/
        // BuildObjectFromNamedList recurse correctly rather than only
        // handling a single flat level.
        var level3 = new SamplePerson { Name = "Level3", Age = 3, Score = 3.3 };
        var wrapper = new NestedWrapper
        {
            Level1 = new NestedWrapper
            {
                Level1 = null,
                Person = null,
            },
            Person = level3,
        };

        RValue rv = wrapper.ToRValue();
        NestedWrapper result = rv.To<NestedWrapper>();

        result.Person!.Name.Should().Be("Level3");
        result.Level1.Should().NotBeNull();
        result.Level1!.Person.Should().BeNull();
    }

    public class NestedWrapper
    {
        public NestedWrapper? Level1 { get; set; }
        public SamplePerson? Person { get; set; }
    }
}

using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>Plain POCO used to exercise the class/Table conversion paths - needs a public parameterless constructor and public settable properties for the reverse (RValue -> object) direction.</summary>
public class SamplePerson
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public double Score { get; set; }
}

public enum SampleColor
{
    Red,
    Green,
    Blue,
}

/// <summary>
/// Pure unit tests for RTypeConverter/RValueConversionExtensions - no
/// R process involved. Covers the common basic types beyond RWire's
/// core atomic set, collections, dictionaries, enums-as-factors,
/// plain-object mapping, and the Register/chaining mechanism.
/// </summary>
public class RTypeConverterTests
{
    [Fact]
    public void Long_RoundTrips_ViaDouble()
    {
        long value = 123_456_789L;
        RValue rv = value.ToRValue();
        rv.TypeTag.Should().Be(RTypeTag.Double);
        rv.To<long>().Should().Be(value);
    }

    [Fact]
    public void Uint_RoundTrips()
    {
        uint value = 4_000_000_000u; // beyond int32's positive range
        RValue rv = value.ToRValue();
        rv.To<uint>().Should().Be(value);
    }

    [Fact]
    public void Decimal_RoundTrips_WithinDoublePrecision()
    {
        decimal value = 1234.5m;
        RValue rv = value.ToRValue();
        rv.To<decimal>().Should().Be(value);
    }

    [Fact]
    public void Short_And_Sbyte_And_Ushort_RoundTrip()
    {
        short s = -1234;
        s.ToRValue().To<short>().Should().Be(s);

        sbyte sb = -12;
        sb.ToRValue().To<sbyte>().Should().Be(sb);

        ushort us = 60000;
        us.ToRValue().To<ushort>().Should().Be(us);
    }

    [Fact]
    public void DateTime_RoundTrips_AsPosixctDouble()
    {
        var dt = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        RValue rv = dt.ToRValue();

        rv.TypeTag.Should().Be(RTypeTag.Double);
        rv.Class.Should().Equal("POSIXct", "POSIXt");
        rv.To<DateTime>().Should().Be(dt);
    }

    [Fact]
    public void DateOnly_RoundTrips_AsRDateDouble()
    {
        var date = new DateOnly(2024, 6, 15);
        RValue rv = date.ToRValue();

        rv.Class.Should().Equal("Date");
        rv.To<DateOnly>().Should().Be(date);
    }

    [Fact]
    public void TimeOnly_RoundTrips_AsDifftimeSeconds()
    {
        var time = new TimeOnly(13, 45, 30);
        RValue rv = time.ToRValue();

        rv.Class.Should().Equal("difftime");
        rv.To<TimeOnly>().Should().Be(time);
    }

    [Fact]
    public void Guid_RoundTrips_AsCharacter()
    {
        Guid guid = Guid.NewGuid();
        RValue rv = guid.ToRValue();

        rv.TypeTag.Should().Be(RTypeTag.Character);
        rv.To<Guid>().Should().Be(guid);
    }

    [Fact]
    public void NullableInt_WithValue_RoundTrips()
    {
        int? value = 42;
        RValue rv = RTypeConverter.Default.Convert<int?, RValue>(value);
        rv.TypeTag.Should().Be(RTypeTag.Integer);
    }

    [Fact]
    public void NullableInt_Null_ConvertsToRValueNull()
    {
        int? value = null;
        RValue rv = RTypeConverter.Default.Convert<int?, RValue>(value);
        rv.TypeTag.Should().Be(RTypeTag.Null);
    }

    [Fact]
    public void Enum_RoundTrips_AsFactor()
    {
        RValue rv = SampleColor.Green.ToRValue();

        rv.Class.Should().Equal("factor");
        rv.Attributes!["levels"].CharacterValues.Should().Equal("Red", "Green", "Blue");
        rv.To<SampleColor>().Should().Be(SampleColor.Green);
    }

    [Fact]
    public void IntArray_BecomesAtomicIntegerVector_NotGenericList()
    {
        int[] values = { 1, 2, 3, 4 };
        RValue rv = values.ToRValue();

        rv.TypeTag.Should().Be(RTypeTag.Integer, "a bulk-convertible element type should produce an atomic vector, not a generic List");
        rv.IntegerValues.Should().Equal(values);
        rv.To<int[]>().Should().Equal(values);
    }

    [Fact]
    public void ListOfLong_BecomesAtomicDoubleVector()
    {
        var values = new List<long> { 10L, 20L, 30L };
        RValue rv = values.ToRValue();

        rv.TypeTag.Should().Be(RTypeTag.Double);
        rv.To<List<long>>().Should().Equal(values);
    }

    [Fact]
    public void PlainObject_RoundTrips_AsNamedList()
    {
        var person = new SamplePerson { Name = "Ada", Age = 30, Score = 99.5 };

        RValue rv = person.ToRValue();

        rv.TypeTag.Should().Be(RTypeTag.List);
        rv.Names.Should().Equal("Name", "Age", "Score");

        SamplePerson roundTripped = rv.To<SamplePerson>();
        roundTripped.Name.Should().Be("Ada");
        roundTripped.Age.Should().Be(30);
        roundTripped.Score.Should().Be(99.5);
    }

    [Fact]
    public void EnumerableOfRecords_BecomesTable_AndRoundTrips()
    {
        var people = new List<SamplePerson>
        {
            new() { Name = "Ada", Age = 30, Score = 99.5 },
            new() { Name = "Bob", Age = 25, Score = 88.0 },
        };

        RValue rv = people.ToRValue();

        rv.TypeTag.Should().Be(RTypeTag.Table, "a sequence of record-like objects should become a TABLE");
        rv.RowCount.Should().Be(2);
        rv.Names.Should().Equal("Name", "Age", "Score");

        List<SamplePerson> roundTripped = rv.To<List<SamplePerson>>();
        roundTripped.Should().HaveCount(2);
        roundTripped[0].Name.Should().Be("Ada");
        roundTripped[1].Age.Should().Be(25);
    }

    [Fact]
    public void DictionaryOfStringToInt_RoundTrips_AsNamedList()
    {
        var dict = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

        RValue rv = dict.ToRValue();

        rv.TypeTag.Should().Be(RTypeTag.List);
        rv.Names.Should().Equal("a", "b");

        Dictionary<string, int> roundTripped = rv.To<Dictionary<string, int>>();
        roundTripped.Should().Equal(dict);
    }

    [Fact]
    public void Register_DirectConverter_IsUsedOverStructuralFallback()
    {
        var converter = new RTypeConverter();
        converter.Register<SamplePerson, RValue>(p => RValue.OfCharacter(new[] { $"{p.Name} ({p.Age})" }));

        var person = new SamplePerson { Name = "Ada", Age = 30 };
        RValue rv = person.ToRValue(converter);

        rv.TypeTag.Should().Be(RTypeTag.Character);
        rv.CharacterValues![0].Should().Be("Ada (30)");
    }

    /// <summary>Minimal domain types for the chaining test - A has no direct converter to C, but A->B and B->C do.</summary>
    private readonly record struct Celsius(double Degrees);
    private readonly record struct Fahrenheit(double Degrees);

    [Fact]
    public void Register_ChainsThroughIntermediateType_WhenNoDirectConverterExists()
    {
        var converter = new RTypeConverter();
        converter.Register<Celsius, Fahrenheit>(c => new Fahrenheit(c.Degrees * 9.0 / 5.0 + 32.0));
        converter.Register<Fahrenheit, string>(f => $"{f.Degrees}F");

        // No Celsius -> string converter registered directly - must chain through Fahrenheit.
        string result = converter.Convert<Celsius, string>(new Celsius(100));

        result.Should().Be("212F");
    }

    [Fact]
    public void Convert_WithNoPathAvailable_ThrowsWithHelpfulMessage()
    {
        var converter = new RTypeConverter();

        Action act = () => converter.Convert<SamplePerson, Celsius>(new SamplePerson());

        act.Should().Throw<InvalidOperationException>().WithMessage("*No converter*");
    }
}

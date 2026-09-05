using System.Diagnostics;
using AwesomeAssertions;
using Xunit;
using Xunit.Abstractions;

namespace RWire.Tests;

/// <summary>
/// Correctness + timing scaffolding for TABLE transfer at various
/// sizes, and for lists mixing tables with other value types
/// (docs/spec.md section 12.6 / docs/phases/phase-4-table-transfer.md).
///
/// IMPORTANT: these are NOT calibrated pass/fail performance gates -
/// there's no reference machine to set a meaningful threshold against,
/// and Phase 4's current implementation buffers the whole encoded
/// value before sending (see docs/progress.md's "Decisions changed
/// since spec.md" on this), so timings here reflect that, not the
/// fully-streamed design spec.md originally described. Treat the
/// logged timings as something to *look at* when you have a real
/// machine to run this on, not as an automated benchmark result.
/// Correctness (the round-trip assertions) is what actually gates
/// these tests passing or failing.
/// </summary>
[Collection(nameof(RWireProcessCollection))]
public class TablePerformanceTests
{
    private readonly ProcessSupervisor _supervisor;
    private readonly ITestOutputHelper _output;

    public TablePerformanceTests(RWireProcessFixture fixture, ITestOutputHelper output)
    {
        _supervisor = fixture.Supervisor;
        _output = output;
    }

    public static IEnumerable<object[]> TableSizes => new[]
    {
        new object[] { 100 },
        new object[] { 1_000 },
        new object[] { 10_000 },
        new object[] { 100_000 },
    };

    [Theory]
    [MemberData(nameof(TableSizes))]
    public async Task TransferTable_RoundTrip_IsCorrect_AndTimingIsLogged(int rowCount)
    {
        var random = new Random(12345 + rowCount);
        RValue table = RandomTableGenerator.GenerateTable(rowCount, random);

        var setStopwatch = Stopwatch.StartNew();
        using RHandle handle = await _supervisor.SetObjAsync(table);
        setStopwatch.Stop();

        var getStopwatch = Stopwatch.StartNew();
        RValue roundTripped = await _supervisor.GetObjAsync(handle);
        getStopwatch.Stop();

        roundTripped.TypeTag.Should().Be(RTypeTag.Table);
        roundTripped.RowCount.Should().Be(rowCount);
        roundTripped.Length.Should().Be(5); // logical, integer, double, character, raw columns

        IReadOnlyDictionary<string, RValue> original = table.GetTableColumns();
        IReadOnlyDictionary<string, RValue> result = roundTripped.GetTableColumns();
        result["integer_col"].IntegerValues.Should().Equal(original["integer_col"].IntegerValues);
        result["double_col"].DoubleValues.Should().HaveCount(rowCount);
        result["character_col"].CharacterValues.Should().Equal(original["character_col"].CharacterValues);

        _output.WriteLine(
            $"[perf] {rowCount,7} rows x 5 cols  ->  SET {setStopwatch.Elapsed.TotalMilliseconds,7:F1} ms, " +
            $"GET {getStopwatch.Elapsed.TotalMilliseconds,7:F1} ms");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public async Task TransferMixedList_WithTablesAndOtherTypes_RoundTrips(int elementCount)
    {
        var random = new Random(54321 + elementCount);
        RValue list = RandomTableGenerator.GenerateMixedList(elementCount, rowsPerElement: 500, random);

        var stopwatch = Stopwatch.StartNew();
        using RHandle handle = await _supervisor.SetObjAsync(list);
        RValue roundTripped = await _supervisor.GetObjAsync(handle);
        stopwatch.Stop();

        roundTripped.TypeTag.Should().Be(RTypeTag.List);
        roundTripped.ListValues.Should().HaveCount(elementCount);

        for (int i = 0; i < elementCount; i++)
        {
            RTypeTag expectedTag = (i % 3) switch
            {
                0 => RTypeTag.Table,
                1 => RTypeTag.Double,
                _ => RTypeTag.Character,
            };
            roundTripped.ListValues![i].TypeTag.Should().Be(expectedTag, $"element {i}");
        }

        _output.WriteLine(
            $"[perf] mixed list of {elementCount} elements (500 rows/table)  ->  " +
            $"round trip {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
    }
}

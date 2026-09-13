using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Round-trips a fresh block of random bytes through the R worker (via
/// base R's `identity()`, so no worker.R changes are needed) M times
/// per case, checking the data comes back byte-for-byte identical each
/// time. This is a correctness check first and a rough, informal speed
/// gauge second - like TablePerformanceTests, there's no reference
/// machine to calibrate a pass/fail threshold against, so the timing
/// is only logged via ITestOutputHelper, never asserted on. See
/// SyncVsAsyncBenchmarkTests/TablePerformanceTests for the same
/// pattern applied to other transfer shapes.
///
/// Uses the shared RWireProcessFixture (one R process for every test
/// in this class) rather than starting a fresh process per case, since
/// process-launch overhead would otherwise dominate the very timings
/// this test logs.
/// </summary>
[Collection(nameof(RWireProcessCollection))]
public class RawByteRoundTripTests
{
    private readonly ProcessSupervisor _supervisor;
    private readonly ITestOutputHelper _output;

    public RawByteRoundTripTests(RWireProcessFixture fixture, ITestOutputHelper output)
    {
        _supervisor = fixture.Supervisor;
        _output = output;
    }

    /// <summary>(byteCount per round trip, number of round trips).</summary>
    public static IEnumerable<object[]> Cases =>
    [
        [1_000, 50],
        [100_000, 20],
        [1_000_000, 5],
        [5_000_000, 2],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RandomBytes_RoundTripMTimes_ComeBackIdentical_AndTimingIsLogged(int byteCount, int iterations)
    {
        // Fixed seed: a failure reproduces deterministically instead of
        // depending on whichever random bytes happened to be drawn.
        var random = new Random(12345 + byteCount);
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            byte[] original = new byte[byteCount];
            random.NextBytes(original);

            RValue sent = RValue.OfRaw(original);
            RValue echoed = await _supervisor.CallAsync(
                "identity",
                new RCallArgument[] { sent },
                TestContext.Current.CancellationToken);

            echoed.TypeTag.Should().Be(RTypeTag.Raw, $"iteration {i}");
            echoed.RawValues.Should().Equal(original, $"iteration {i}");
        }

        stopwatch.Stop();

        // Round trip: byteCount bytes go out AND come back per
        // iteration, so *2 for the total bytes actually moved.
        double totalMegabytes = byteCount * (double)iterations * 2 / (1024 * 1024);
        double megabytesPerSecond = totalMegabytes / stopwatch.Elapsed.TotalSeconds;
        _output.WriteLine(
            $"[perf] {byteCount,9:N0} bytes x {iterations,3} round trips -> " +
            $"{stopwatch.Elapsed.TotalMilliseconds,8:F1} ms total, ~{megabytesPerSecond,6:F1} MB/s");
    }
}

using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Latency scaffolding for the sync-vs-async question docs/spec.md
/// section 9 leaves open: "validate against the async path with the
/// benchmark in section 12.6 rather than assuming" blocking I/O is
/// faster for this workload. Like TablePerformanceTests, this logs
/// timings via ITestOutputHelper rather than asserting a threshold -
/// there's no reference machine here to calibrate a meaningful
/// pass/fail bound against, and which path is actually faster is
/// exactly the open question this exists to let a real run answer,
/// not something to bake in as an assumption on either side.
///
/// The workload here is deliberately many small, frequent calls (a
/// trivial EVAL, repeated) rather than large payloads - that's the
/// regime where interpreter/call-overhead is a meaningful fraction of
/// total time and where the sync-vs-async choice is most likely to
/// show a real difference; TablePerformanceTests already covers the
/// large-payload regime, where throughput is dominated by memcpy/
/// socket cost regardless of which path is used.
/// </summary>
[Collection(nameof(RWireProcessCollection))]
public class SyncVsAsyncBenchmarkTests
{
    private readonly ProcessSupervisor _supervisor;
    private readonly ITestOutputHelper _output;

    public SyncVsAsyncBenchmarkTests(RWireProcessFixture fixture, ITestOutputHelper output)
    {
        _supervisor = fixture.Supervisor;
        _output = output;
    }

    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    public void Eval_Sync_RepeatedSmallCalls_TimingIsLogged(int iterationCount)
    {
        // A brief warm-up iteration (excluded from timing) absorbs
        // one-time JIT/first-call costs so the logged number reflects
        // steady-state per-call latency, not warm-up noise.
        _supervisor.Eval("1 + 1");

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterationCount; i++)
        {
            RValue result = _supervisor.Eval("1 + 1");
            result.DoubleValues![0].Should().Be(2.0);
        }
        stopwatch.Stop();

        double perCallMicroseconds = stopwatch.Elapsed.TotalMicroseconds / iterationCount;
        _output.WriteLine(
            $"[perf] sync Eval x{iterationCount,4}  ->  total {stopwatch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"{perCallMicroseconds:F1} us/call");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    public async Task EvalAsync_RepeatedSmallCalls_TimingIsLogged(int iterationCount)
    {
        await _supervisor.EvalAsync("1 + 1", TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterationCount; i++)
        {
            RValue result = await _supervisor.EvalAsync("1 + 1", TestContext.Current.CancellationToken);
            result.DoubleValues![0].Should().Be(2.0);
        }
        stopwatch.Stop();

        double perCallMicroseconds = stopwatch.Elapsed.TotalMicroseconds / iterationCount;
        _output.WriteLine(
            $"[perf] async EvalAsync x{iterationCount,4}  ->  total {stopwatch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"{perCallMicroseconds:F1} us/call");
    }

    [Fact]
    public async Task Eval_Sync_Vs_EvalAsync_HeadToHead_OnSameConnection_TimingIsLogged()
    {
        // Both paths measured back-to-back against the *same* live
        // connection in one test run, rather than comparing across
        // separate test-method runs, to reduce noise from
        // run-to-run machine/JIT variance when eyeballing the two
        // numbers together.
        const int iterations = 200;

        _supervisor.Eval("1 + 1"); // warm-up
        var syncStopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            _supervisor.Eval("1 + 1");
        }
        syncStopwatch.Stop();

        await _supervisor.EvalAsync("1 + 1", TestContext.Current.CancellationToken); // warm-up
        var asyncStopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await _supervisor.EvalAsync("1 + 1", TestContext.Current.CancellationToken);
        }
        asyncStopwatch.Stop();

        double syncPerCall = syncStopwatch.Elapsed.TotalMicroseconds / iterations;
        double asyncPerCall = asyncStopwatch.Elapsed.TotalMicroseconds / iterations;

        _output.WriteLine(
            $"[perf] head-to-head x{iterations}  ->  sync {syncPerCall:F1} us/call, " +
            $"async {asyncPerCall:F1} us/call, ratio {asyncPerCall / syncPerCall:F2}x");
    }
}

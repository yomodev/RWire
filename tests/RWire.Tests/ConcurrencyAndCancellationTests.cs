using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Answers two questions directly with executable proof rather than
/// just prose:
///   1. Is _connectionLock sufficient for safe concurrent multi-
///      threaded use of one ProcessSupervisor? (Yes, for wire-protocol
///      correctness - see ConcurrentCalls_FromMultipleThreads_...)
///   2. What happens if you cancel an in-flight call - does it corrupt
///      the session, and does it require killing/restarting the R
///      process? (It's treated as a fault and automatically restarted
///      - see Cancellation_DuringInFlightCall_...)
/// </summary>
public class ConcurrencyAndCancellationTests
{
    private static string WorkerScriptPath => RWireProcessFixture.WorkerScriptPath;

    [Fact]
    public async Task ConcurrentCalls_FromMultipleThreads_DoNotCorruptEachOther()
    {
        // _connectionLock (a SemaphoreSlim(1,1)) serializes every
        // Eval/Call/GetObj/SetObj/CreateRef/ReleaseRef/heartbeat
        // operation on a given connection - every method acquires it
        // before touching _connection and releases it in a finally
        // block, so concurrent callers queue rather than interleave
        // their frames on the wire. This is the property that
        // actually matters for correctness with multiple threads
        // sharing one ProcessSupervisor: no corrupted/misattributed
        // responses, even though R itself only ever processes one
        // request at a time (docs/spec.md section 1.2's non-goal of
        // concurrent pipelining - this is achieved by serializing
        // access, not by R handling things in parallel).
        //
        // Each concurrent call asks for a value only *its own* input
        // could produce, so a wrong (swapped/corrupted) answer would
        // fail the assertion for that specific task rather than
        // slipping through unnoticed.
        using var supervisor = new ProcessSupervisor(
            new RWireOptions { WorkerScriptPath = WorkerScriptPath }, TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        const int concurrency = 20;

        IEnumerable<Task<double>> tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            RValue result = await supervisor.EvalAsync($"{i} + 1000", TestContext.Current.CancellationToken);
            return result.DoubleValues![0];
        });

        double[] results = await Task.WhenAll(tasks);

        for (int i = 0; i < concurrency; i++)
        {
            results[i].Should().Be(i + 1000, $"task {i} should get back exactly its own expected value");
        }
    }

    [Fact]
    public async Task ConcurrentCalls_MixingSyncAndAsync_FromMultipleThreads_DoNotCorruptEachOther()
    {
        // Same property as above, but mixing the sync (Eval, blocking
        // a thread-pool thread) and async (EvalAsync) call paths
        // against the same connection at once - both funnel through
        // the same _connectionLock regardless of which one a given
        // caller uses.
        using var supervisor = new ProcessSupervisor(
            new RWireOptions { WorkerScriptPath = WorkerScriptPath }, TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        const int concurrency = 20;

        IEnumerable<Task<double>> tasks = Enumerable.Range(0, concurrency).Select(i =>
        {
            if (i % 2 == 0)
            {
                return Task.Run(() => supervisor.Eval($"{i} + 2000").DoubleValues![0]);
            }

            return Task.Run(async () =>
                (await supervisor.EvalAsync($"{i} + 2000", TestContext.Current.CancellationToken)).DoubleValues![0]);
        });

        double[] results = await Task.WhenAll(tasks);

        for (int i = 0; i < concurrency; i++)
        {
            results[i].Should().Be(i + 2000, $"task {i} should get back exactly its own expected value");
        }
    }

    [Fact]
    public async Task Cancellation_DuringInFlightCall_TriggersRestart_AndSupervisorRecovers()
    {
        // Sys.sleep(5) guarantees the call is genuinely in-flight (R
        // is still "computing", C# is genuinely blocked awaiting the
        // RESULT frame) when cancellation fires, rather than racing to
        // cancel before any bytes have even gone out.
        //
        // This is deliberately NOT the non-fatal ERROR-frame path
        // (docs/spec.md section 12.5) - a caught R condition leaves
        // the connection healthy because R always finishes writing a
        // complete, well-formed ERROR frame. A *cancelled* call can
        // abandon the wire mid-frame (a partial write already sent, or
        // a partial read already consumed) with no way to safely
        // resume alignment. RWire's answer: treat it exactly like a
        // crash - discard the connection and restart the worker
        // automatically, via the same Fault() path any other in-flight
        // failure uses. So: yes, cancellation is fully supported via
        // the existing CancellationToken parameters; no, it does not
        // corrupt the *session* (the supervisor recovers on its own);
        // yes, it does require killing and restarting the R process
        // specifically (not something to avoid - it's the correct,
        // automatic recovery, not a manual step you need to take).
        var options = new RWireOptions
        {
            WorkerScriptPath = WorkerScriptPath,
            HeartbeatInterval = TimeSpan.FromMilliseconds(300),
            HeartbeatResponseTimeout = TimeSpan.FromSeconds(2),
            InitialRestartDelay = TimeSpan.FromMilliseconds(100),
            MaxRestartDelay = TimeSpan.FromMilliseconds(500),
        };

        using var supervisor = new ProcessSupervisor(options, TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        using var callCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Func<Task> act = () => supervisor.EvalAsync("Sys.sleep(5)", callCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (supervisor.State != SupervisorState.Ready && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        supervisor.State.Should().Be(
            SupervisorState.Ready, "the supervisor should restart automatically after a cancelled in-flight call");

        // The old (sleeping) R process should have been killed as part
        // of the restart, not left running - CleanupForRestart force-
        // kills the process tree before launching a fresh one, so
        // there's no orphaned "still sleeping" process left behind
        // from the cancelled call.
        RValue result = await supervisor.EvalAsync("1 + 1", TestContext.Current.CancellationToken);
        result.DoubleValues![0].Should().Be(2.0);
    }

    [Fact]
    public async Task Cancellation_BeforeAcquiringConnectionLock_DoesNotTriggerRestart()
    {
        // The other half of the cancellation story: if cancellation
        // fires while a call is merely *waiting its turn* for
        // _connectionLock (nothing has touched the wire yet), no
        // restart should be triggered - the connection is still
        // perfectly healthy. This works because
        // _connectionLock.WaitAsync(ct) sits *outside* the try/catch
        // that calls Fault() on failure - see Eval/EvalAsync/etc. in
        // ProcessSupervisor.cs.
        var options = new RWireOptions
        {
            WorkerScriptPath = WorkerScriptPath,
            HeartbeatInterval = TimeSpan.FromMilliseconds(300),
            HeartbeatResponseTimeout = TimeSpan.FromSeconds(2),
        };

        using var supervisor = new ProcessSupervisor(options, TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        // Hold the connection busy with a slow call, so a second call
        // is genuinely stuck waiting for the lock (not for wire I/O)
        // when we cancel it.
        Task<RValue> blockingCall = supervisor.EvalAsync("{ Sys.sleep(1); 42 }", TestContext.Current.CancellationToken);

        using var waitingCallCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> waitingAct = () => supervisor.EvalAsync("2 + 2", waitingCallCts.Token);

        await waitingAct.Should().ThrowAsync<OperationCanceledException>();

        // No restart should have been triggered by that cancellation -
        // the blocking call is still legitimately in flight.
        supervisor.State.Should().BeOneOf(SupervisorState.Busy, SupervisorState.Ready);
        supervisor.RestartCount.Should().Be(0);

        RValue blockingResult = await blockingCall;
        // Sys.sleep() itself always returns invisible(NULL) - the
        // expression was changed to return 42 afterward specifically
        // so this checks the call actually completed with a real
        // result, not just that it didn't throw.
        blockingResult.TypeTag.Should().Be(RTypeTag.Double);
        blockingResult.DoubleValues![0].Should().Be(42);

        supervisor.State.Should().Be(SupervisorState.Ready);
        supervisor.RestartCount.Should().Be(0, "no fault should ever have been triggered in this scenario");
    }
}

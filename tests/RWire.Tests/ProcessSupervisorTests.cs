using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Phase 1 handshake/heartbeat/shutdown tests plus Phase 6 automatic-
/// restart tests (docs/phases/phase-6-process-supervision.md). These
/// deliberately do NOT use the shared RWireProcessFixture - each test
/// here controls a full process lifecycle (including some that never
/// successfully start, or that deliberately kill the process
/// mid-test), which the shared fixture is not compatible with.
/// Requires a real R installation with Rscript on PATH; see
/// FrameCodecTests/RConnectionTests for the unit-level protocol
/// coverage that doesn't.
/// </summary>
public class ProcessSupervisorTests
{
    private static string WorkerScriptPath => RWireProcessFixture.WorkerScriptPath;

    private static RWireOptions FastHeartbeatOptions() => new()
    {
        WorkerScriptPath = WorkerScriptPath,
        HeartbeatInterval = TimeSpan.FromMilliseconds(300),
        HeartbeatResponseTimeout = TimeSpan.FromSeconds(2),
    };

    /// <summary>Fast backoff settings for restart tests, so they don't spend most of their time in Task.Delay.</summary>
    private static RWireOptions FastRestartOptions() => new()
    {
        WorkerScriptPath = WorkerScriptPath,
        HeartbeatInterval = TimeSpan.FromMilliseconds(300),
        HeartbeatResponseTimeout = TimeSpan.FromSeconds(2),
        InitialRestartDelay = TimeSpan.FromMilliseconds(100),
        MaxRestartDelay = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public async Task StartAsync_CompletesHelloHandshake_AndReachesReadyState()
    {
        var options = new RWireOptions { WorkerScriptPath = WorkerScriptPath };

        using var supervisor = new ProcessSupervisor(options, TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        supervisor.State.Should().Be(SupervisorState.Ready);
        supervisor.Port.Should().BePositive();
        supervisor.RVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartAsync_WithMissingRScriptExecutable_ThrowsRatherThanHanging()
    {
        var options = new RWireOptions
        {
            RScriptPath = "this-executable-should-not-exist-rwire-test",
            WorkerScriptPath = WorkerScriptPath,
            HandshakeTimeout = TimeSpan.FromSeconds(3),
        };

        using var supervisor = new ProcessSupervisor(options, TestLogging.CreateProcessSupervisorLogger());

        Func<Task> act = () => supervisor.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        supervisor.State.Should().Be(SupervisorState.Faulted);
    }

    [Fact]
    public async Task StartAsync_WithMissingWorkerScript_TimesOutRatherThanHanging()
    {
        var options = new RWireOptions
        {
            WorkerScriptPath = "this-script-does-not-exist.R",
            HandshakeTimeout = TimeSpan.FromSeconds(3),
        };

        using var supervisor = new ProcessSupervisor(options, TestLogging.CreateProcessSupervisorLogger());

        Func<Task> act = () => supervisor.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutException>();
        supervisor.State.Should().Be(SupervisorState.Faulted);
    }

    [Fact]
    public async Task Heartbeat_KeepsConnectionAlive_OverMultipleIntervals()
    {
        using var supervisor = new ProcessSupervisor(FastHeartbeatOptions(), TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        supervisor.State.Should().Be(SupervisorState.Ready);
    }

    [Fact]
    public async Task Dispose_SendsGracefulShutdown_AndProcessExitsCleanly()
    {
        var options = new RWireOptions { WorkerScriptPath = WorkerScriptPath };
        var supervisor = new ProcessSupervisor(options, TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        supervisor.Dispose();

        // ExitCode is captured by Dispose() before the underlying
        // Process object is itself disposed - reading Process
        // properties after Dispose throws ("No process is associated
        // with this object"), so ProcessSupervisor surfaces the value
        // proactively instead.
        supervisor.ExitCode.Should().Be(0);
        supervisor.State.Should().Be(SupervisorState.Disposed);
    }

    [Fact]
    public async Task DiagnosticOutput_CapturesOutput_FromWorkerScript()
    {
        // Replaces an earlier version of this test that pointed
        // WorkerScriptPath at a *missing* file and asserted some
        // diagnostic output would appear - manual testing on Windows
        // showed that's false: `Rscript this-script-does-not-exist.R`
        // prints nothing at all there (confirmed: just a blank line
        // back to the prompt), so the whole premise was wrong on that
        // platform. Instead, this uses a real temporary R script that
        // *executes* and explicitly writes a deterministic line before
        // exiting - that's testable on every platform, unlike relying
        // on how (or whether) a missing-file error gets reported.
        string tempScriptPath = Path.Combine(Path.GetTempPath(), $"rwire-diagnostic-test-{Guid.NewGuid():N}.R");
        await File.WriteAllTextAsync(
            tempScriptPath,
            "cat('deliberate diagnostic line for RWire test\\n')\n" +
            "quit(status = 1, save = 'no')\n",
            TestContext.Current.CancellationToken);

        try
        {
            var options = new RWireOptions
            {
                WorkerScriptPath = tempScriptPath,
                HandshakeTimeout = TimeSpan.FromSeconds(3),
            };

            using var supervisor = new ProcessSupervisor(options, TestLogging.CreateProcessSupervisorLogger());

            var allLines = new List<string>();
            supervisor.DiagnosticOutput += (line, _) => allLines.Add(line);

            // The script exits immediately without ever sending a
            // HELLO frame, so StartAsync times out waiting for the
            // handshake - same shape as
            // StartAsync_WithMissingWorkerScript_TimesOutRatherThanHanging,
            // but this time guaranteed to produce diagnostic output.
            Func<Task> act = () => supervisor.StartAsync(TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<TimeoutException>();

            // Poll rather than a fixed delay - the async stdio pump
            // tasks (PumpStreamAsync) run independently of StartAsync's
            // own timeout.
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (allLines.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            allLines.Should().Contain(line => line.Contains("deliberate diagnostic line for RWire test"));
        }
        finally
        {
            File.Delete(tempScriptPath);
        }
    }

    // -----------------------------------------------------------------
    // Phase 6 - automatic restart (docs/phases/phase-6-process-supervision.md)
    // -----------------------------------------------------------------

    [Fact]
    public async Task ExternalProcessKill_TriggersAutomaticRestart_AndSupervisorRecovers()
    {
        using var supervisor = new ProcessSupervisor(FastRestartOptions(), TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        supervisor.State.Should().Be(SupervisorState.Ready);

        ulong originalSessionId = supervisor.SessionId;

        supervisor.ProcessForTesting.Kill(entireProcessTree: true);

        // First: the crash should be observed (State leaves Ready).
        DateTime observedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (supervisor.State == SupervisorState.Ready && DateTime.UtcNow < observedDeadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        supervisor.State.Should().NotBe(SupervisorState.Ready, "the external kill should have been detected");

        // Then: the supervisor should recover on its own, without any
        // caller intervention.
        DateTime recoveredDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (supervisor.State != SupervisorState.Ready && DateTime.UtcNow < recoveredDeadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        supervisor.State.Should().Be(SupervisorState.Ready, "the supervisor should have restarted automatically");
        supervisor.RestartCount.Should().BeGreaterThanOrEqualTo(1);
        supervisor.SessionId.Should().NotBe(originalSessionId, "a successful restart should mint a new session");

        // The recovered connection should genuinely work, not just report Ready.
        RValue result = await supervisor.EvalAsync("1 + 1", TestContext.Current.CancellationToken);
        result.DoubleValues![0].Should().Be(2.0);
    }

    [Fact]
    public async Task AfterAutomaticRestart_OldHandlesAreRejected_ViaSessionIdMismatch()
    {
        using var supervisor = new ProcessSupervisor(FastRestartOptions(), TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        RHandle handleFromBeforeCrash = await supervisor.SetObjAsync(
            RValue.OfDouble([1.0]), TestContext.Current.CancellationToken);

        supervisor.ProcessForTesting.Kill(entireProcessTree: true);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (supervisor.State != SupervisorState.Ready && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        supervisor.State.Should().Be(SupervisorState.Ready, "the supervisor should have restarted automatically");

        Func<Task> act = () => supervisor.GetObjAsync(handleFromBeforeCrash, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task CallMadeDuringRestart_WaitsForRecovery_ThenSucceeds()
    {
        var options = new RWireOptions
        {
            WorkerScriptPath = WorkerScriptPath,
            HeartbeatInterval = TimeSpan.FromMilliseconds(300),
            HeartbeatResponseTimeout = TimeSpan.FromSeconds(2),
            InitialRestartDelay = TimeSpan.FromMilliseconds(100),
            MaxRestartDelay = TimeSpan.FromMilliseconds(500),
            RestartWaitTimeout = TimeSpan.FromSeconds(15),
        };

        using var supervisor = new ProcessSupervisor(options, TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        supervisor.ProcessForTesting.Kill(entireProcessTree: true);

        // Deliberately calls in shortly after the kill - while the
        // restart is plausibly still in progress - rather than after
        // waiting for full recovery, to exercise EnsureReady's
        // wait-for-restart behavior specifically (docs/spec.md
        // section 11, Phase 6 exit criteria: recovery is transparent
        // to "the next call", not something the caller manages).
        await Task.Delay(100, TestContext.Current.CancellationToken);

        RValue result = await supervisor.EvalAsync("2 + 2", TestContext.Current.CancellationToken);

        result.DoubleValues![0].Should().Be(4.0);
    }

    [Fact]
    public async Task RestartExhaustion_AfterMaxAttempts_BecomesPermanentlyFailed()
    {
        // A channel listener factory that works once (the initial
        // launch) and fails on every subsequent call (every restart
        // attempt) - simulates a worker that can never successfully
        // reconnect, to exercise the give-up-after-MaxRestartAttempts
        // path without needing the real R installation to actually be
        // broken.
        int callCount = 0;
        IRChannelListener Factory()
        {
            callCount++;
            return callCount == 1 ? new TcpRChannelListener() : new AlwaysFailingChannelListener();
        }

        var options = new RWireOptions
        {
            WorkerScriptPath = WorkerScriptPath,
            MaxRestartAttempts = 2,
            InitialRestartDelay = TimeSpan.FromMilliseconds(50),
            MaxRestartDelay = TimeSpan.FromMilliseconds(100),
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            HeartbeatResponseTimeout = TimeSpan.FromSeconds(1),
        };

        using var supervisor = new ProcessSupervisor(options, Factory, TestLogging.CreateProcessSupervisorLogger());
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        supervisor.ProcessForTesting.Kill(entireProcessTree: true);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!supervisor.IsPermanentlyFailed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        supervisor.IsPermanentlyFailed.Should().BeTrue();
        supervisor.State.Should().Be(SupervisorState.Faulted);
        supervisor.RestartCount.Should().Be(0, "every restart attempt was sabotaged and should have failed");

        Func<Task> act = () => supervisor.EvalAsync("1 + 1", TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class AlwaysFailingChannelListener : IRChannelListener
    {
        public string ChannelArgument => "0";

        public Task<IRChannel> AcceptAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated listener failure for restart-exhaustion testing.");

        public void Dispose()
        {
        }
    }
}

using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Phase 1 exit-criteria tests (docs/phases/phase-1-channel-protocol.md):
///   - heartbeat keeps the connection alive
///   - killing the R process externally is detected within one
///     heartbeat interval
///   - SHUTDOWN results in clean process exit
///
/// These deliberately do NOT use the shared RWireProcessFixture - each
/// test here controls a full process lifecycle (including some that
/// never successfully start), which the shared fixture is not
/// compatible with. These require a real R installation with Rscript
/// on PATH; see FrameCodecTests/RConnectionTests for the unit-level
/// protocol coverage that doesn't.
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

    [Fact]
    public async Task StartAsync_CompletesHelloHandshake_AndReachesReadyState()
    {
        var options = new RWireOptions { WorkerScriptPath = WorkerScriptPath };

        using var supervisor = new ProcessSupervisor(options);
        await supervisor.StartAsync();

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

        using var supervisor = new ProcessSupervisor(options);

        Func<Task> act = () => supervisor.StartAsync();

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

        using var supervisor = new ProcessSupervisor(options);

        Func<Task> act = () => supervisor.StartAsync();

        await act.Should().ThrowAsync<TimeoutException>();
        supervisor.State.Should().Be(SupervisorState.Faulted);
    }

    [Fact]
    public async Task Heartbeat_KeepsConnectionAlive_OverMultipleIntervals()
    {
        using var supervisor = new ProcessSupervisor(FastHeartbeatOptions());
        await supervisor.StartAsync();

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        supervisor.State.Should().Be(SupervisorState.Ready);
    }

    [Fact]
    public async Task ExternalProcessKill_IsDetectedAsFaulted_WithinHeartbeatWindow()
    {
        using var supervisor = new ProcessSupervisor(FastHeartbeatOptions());
        await supervisor.StartAsync();
        supervisor.State.Should().Be(SupervisorState.Ready);

        supervisor.ProcessForTesting.Kill(entireProcessTree: true);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (supervisor.State == SupervisorState.Ready && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        supervisor.State.Should().Be(SupervisorState.Faulted);
    }

    [Fact]
    public async Task Dispose_SendsGracefulShutdown_AndProcessExitsCleanly()
    {
        var options = new RWireOptions { WorkerScriptPath = WorkerScriptPath };
        var supervisor = new ProcessSupervisor(options);
        await supervisor.StartAsync();

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
    public async Task DiagnosticOutput_CapturesStderr_OnWorkerScriptError()
    {
        var options = new RWireOptions
        {
            WorkerScriptPath = "this-script-does-not-exist.R",
            HandshakeTimeout = TimeSpan.FromSeconds(3),
        };

        using var supervisor = new ProcessSupervisor(options);

        var stderrLines = new List<string>();
        supervisor.DiagnosticOutput += (line, isError) =>
        {
            if (isError) stderrLines.Add(line);
        };

        Func<Task> act = () => supervisor.StartAsync();
        await act.Should().ThrowAsync<TimeoutException>();

        // Poll rather than a fixed delay - the async stdio pump tasks
        // (PumpStreamAsync) run independently of StartAsync's own
        // timeout, so there's no guaranteed instant at which "the
        // R process errored" implies "DiagnosticOutput has already
        // fired" without waiting a little.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (stderrLines.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        stderrLines.Should().NotBeEmpty();
    }
}

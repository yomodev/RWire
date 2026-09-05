using Xunit;

namespace RWire.Tests;

/// <summary>
/// Phase 1 exit-criteria tests (docs/phases/phase-1-channel-protocol.md):
///   - heartbeat keeps the connection alive
///   - killing the R process externally is detected within one
///     heartbeat interval
///   - SHUTDOWN results in clean process exit
///
/// These require a real R installation with Rscript on PATH - they are
/// integration tests, not unit tests (see FrameCodecTests/
/// RConnectionTests for the unit-level protocol coverage).
/// </summary>
public class ProcessSupervisorTests
{
    private static string WorkerScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "r", "worker.R");

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

        Assert.Equal(SupervisorState.Ready, supervisor.State);
        Assert.True(supervisor.Port > 0);
        Assert.False(string.IsNullOrWhiteSpace(supervisor.RVersion));
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync());
        Assert.Equal(SupervisorState.Faulted, supervisor.State);
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

        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.StartAsync());
        Assert.Equal(SupervisorState.Faulted, supervisor.State);
    }

    [Fact]
    public async Task Heartbeat_KeepsConnectionAlive_OverMultipleIntervals()
    {
        using var supervisor = new ProcessSupervisor(FastHeartbeatOptions());
        await supervisor.StartAsync();

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Equal(SupervisorState.Ready, supervisor.State);
    }

    [Fact]
    public async Task ExternalProcessKill_IsDetectedAsFaulted_WithinHeartbeatWindow()
    {
        using var supervisor = new ProcessSupervisor(FastHeartbeatOptions());
        await supervisor.StartAsync();
        Assert.Equal(SupervisorState.Ready, supervisor.State);

        supervisor.ProcessForTesting.Kill(entireProcessTree: true);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (supervisor.State == SupervisorState.Ready && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(SupervisorState.Faulted, supervisor.State);
    }

    [Fact]
    public async Task Dispose_SendsGracefulShutdown_AndProcessExitsCleanly()
    {
        var options = new RWireOptions { WorkerScriptPath = WorkerScriptPath };
        var supervisor = new ProcessSupervisor(options);
        await supervisor.StartAsync();

        System.Diagnostics.Process process = supervisor.ProcessForTesting;

        supervisor.Dispose();

        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(SupervisorState.Disposed, supervisor.State);
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

        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.StartAsync());

        // Give the async stderr reader a moment to flush the line
        // through OutputDataReceived after the process exits.
        await Task.Delay(200);

        Assert.NotEmpty(stderrLines);
    }
}

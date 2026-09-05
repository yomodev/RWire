using Xunit;

namespace RWire.Tests;

/// <summary>
/// Phase 3 handle lifecycle tests (docs/spec.md section 12.4 /
/// docs/phases/phase-3-reference-counting.md's exit criteria).
/// Requires a real R installation with Rscript on PATH.
/// </summary>
public class HandleLifecycleTests : IAsyncLifetime
{
    private ProcessSupervisor _supervisor = null!;

    private static string WorkerScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "r", "worker.R");

    public async Task InitializeAsync()
    {
        _supervisor = new ProcessSupervisor(new RWireOptions { WorkerScriptPath = WorkerScriptPath });
        await _supervisor.StartAsync();
    }

    public Task DisposeAsync()
    {
        _supervisor.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Confirms the R-side registry no longer has an entry for id, via a diagnostic EVAL against the registry environment.</summary>
    private async Task<bool> RegistryContainsAsync(long id)
    {
        RValue result = await _supervisor.EvalAsync(
            $"exists('{id}', envir = .rwire_registry, inherits = FALSE)");
        return result.LogicalCodes![0] == RNumeric.LogicalTrue;
    }

    [Fact]
    public async Task SetObj_ThenGetObj_RoundTripsTheValue()
    {
        using RHandle handle = await _supervisor.SetObjAsync(RValue.OfInteger(new[] { 1, 2, 3 }));

        RValue result = await _supervisor.GetObjAsync(handle);

        Assert.Equal(new[] { 1, 2, 3 }, result.IntegerValues);
    }

    [Fact]
    public async Task Dispose_ReleasesTheHandle_FromTheRSideRegistry()
    {
        RHandle handle = await _supervisor.SetObjAsync(RValue.OfDouble(new double[] { 1.0 }));

        // Grab the id before Dispose invalidates handle.Id.
        long id = await GetHandleIdViaReflectionWorkaroundAsync(handle);

        handle.Dispose();

        // ReleaseHandleBestEffort fires a background Task.Run - give it
        // a moment to complete before checking the registry.
        await WaitUntilAsync(() => RegistryContainsAsync(id).GetAwaiter().GetResult() == false);

        Assert.False(await RegistryContainsAsync(id));
    }

    [Fact]
    public async Task Handle_UsedAfterDispose_Throws()
    {
        RHandle handle = await _supervisor.SetObjAsync(RValue.OfDouble(new double[] { 1.0 }));
        handle.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => _supervisor.GetObjAsync(handle));
    }

    [Fact]
    public async Task TwoHandlesViaCreateRef_BothMustBeDisposed_BeforeObjectIsFreed()
    {
        RHandle first = await _supervisor.SetObjAsync(RValue.OfDouble(new double[] { 42.0 }));
        long id = await GetHandleIdViaReflectionWorkaroundAsync(first);
        RHandle second = await _supervisor.CreateRefAsync(first);

        first.Dispose();
        await Task.Delay(200); // let the background release complete

        Assert.True(
            await RegistryContainsAsync(id),
            "Object should still be registered - the second handle's reference is still live.");

        // Still usable via the second handle.
        RValue result = await _supervisor.GetObjAsync(second);
        Assert.Equal(42.0, result.DoubleValues![0]);

        second.Dispose();
        await WaitUntilAsync(() => RegistryContainsAsync(id).GetAwaiter().GetResult() == false);

        Assert.False(await RegistryContainsAsync(id));
    }

    [Fact]
    public async Task DoubleRelease_IsANoOp_NotAnError()
    {
        // Directly exercises ReleaseRefAsync twice for the same id -
        // the second call must not throw (docs/progress.md: double
        // release is a no-op by design, not a protocol error).
        RHandle handle = await _supervisor.SetObjAsync(RValue.OfDouble(new double[] { 1.0 }));
        long id = await GetHandleIdViaReflectionWorkaroundAsync(handle);

        await _supervisor.ReleaseRefAsync(id);
        await _supervisor.ReleaseRefAsync(id); // should not throw

        Assert.Equal(SupervisorState.Ready, _supervisor.State);
    }

    [Fact]
    public async Task SimulatedCrash_OldHandleFailsFast_AfterManualNewSupervisor()
    {
        // Full automatic restart is Phase 6 - this test only confirms
        // the piece Phase 3 owns: a handle stamped with one
        // supervisor's SessionId is rejected by a *different*
        // supervisor instance (standing in for "after a restart")
        // rather than silently addressing the wrong process's
        // registry.
        RHandle handleFromFirstSession = await _supervisor.SetObjAsync(RValue.OfDouble(new double[] { 1.0 }));

        using var secondSupervisor = new ProcessSupervisor(
            new RWireOptions { WorkerScriptPath = WorkerScriptPath });
        await secondSupervisor.StartAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => secondSupervisor.GetObjAsync(handleFromFirstSession));
    }

    /// <summary>
    /// RHandle.Id is internal by design (not meant for application
    /// code) - tests reach it via InternalsVisibleTo rather than
    /// reflection; the "workaround" name just flags that this is
    /// test-only plumbing, not a suggestion to expose Id publicly.
    /// </summary>
    private static Task<long> GetHandleIdViaReflectionWorkaroundAsync(RHandle handle) =>
        Task.FromResult(handle.Id);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}

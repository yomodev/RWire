using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Phase 3 handle lifecycle tests (docs/spec.md section 12.4). Uses
/// the shared RWireProcessFixture for the "first" supervisor in every
/// test - none of these tests dispose or otherwise disrupt the shared
/// process's lifecycle, they only create/release handles against it.
/// The session-mismatch test still spins up its own second,
/// independent ProcessSupervisor, since testing cross-session
/// rejection requires a genuinely different session by definition.
/// Requires a real R installation with Rscript on PATH.
/// </summary>
[Collection(nameof(RWireProcessCollection))]
public class HandleLifecycleTests
{
    private readonly ProcessSupervisor _supervisor;

    public HandleLifecycleTests(RWireProcessFixture fixture)
    {
        _supervisor = fixture.Supervisor;
    }

    /// <summary>Confirms the R-side registry no longer has an entry for id, via a diagnostic EVAL against the registry environment.</summary>
    private async Task<bool> RegistryContainsAsync(long id)
    {
        RValue result = await _supervisor.EvalAsync(
            $"exists('{id}', envir = .rwire_registry, inherits = FALSE)",
            TestContext.Current.CancellationToken);
        return result.LogicalCodes![0] == RNumeric.LogicalTrue;
    }

    [Fact]
    public async Task SetObj_ThenGetObj_RoundTripsTheValue()
    {
        using RHandle handle = await _supervisor.SetObjAsync(
            RValue.OfInteger(new[] { 1, 2, 3 }), TestContext.Current.CancellationToken);

        RValue result = await _supervisor.GetObjAsync(handle, TestContext.Current.CancellationToken);

        result.IntegerValues.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Dispose_ReleasesTheHandle_FromTheRSideRegistry()
    {
        RHandle handle = await _supervisor.SetObjAsync(
            RValue.OfDouble(new double[] { 1.0 }), TestContext.Current.CancellationToken);
        long id = handle.Id;

        handle.Dispose();

        // ReleaseHandleBestEffort fires a background Task.Run - poll
        // rather than assume it's finished immediately. This relies on
        // EnsureReady() allowing a concurrent EvalAsync call to queue
        // on the connection lock instead of throwing while the
        // background release happens to be Busy - see
        // docs/progress.md's "Decisions changed since spec.md" for
        // that fix.
        await WaitUntilAsync(async () => !await RegistryContainsAsync(id));

        (await RegistryContainsAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UsedAfterDispose_Throws()
    {
        RHandle handle = await _supervisor.SetObjAsync(
            RValue.OfDouble(new double[] { 1.0 }), TestContext.Current.CancellationToken);
        handle.Dispose();

        Func<Task> act = () => _supervisor.GetObjAsync(handle, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task TwoHandlesViaCreateRef_BothMustBeDisposed_BeforeObjectIsFreed()
    {
        RHandle first = await _supervisor.SetObjAsync(
            RValue.OfDouble(new double[] { 42.0 }), TestContext.Current.CancellationToken);
        long id = first.Id;
        RHandle second = await _supervisor.CreateRefAsync(first, TestContext.Current.CancellationToken);

        first.Dispose();
        await WaitUntilAsync(() => Task.FromResult(true), timeoutMs: 200); // let the background release attempt run

        (await RegistryContainsAsync(id)).Should().BeTrue(
            "the object should still be registered - the second handle's reference is still live");

        RValue result = await _supervisor.GetObjAsync(second, TestContext.Current.CancellationToken);
        result.DoubleValues![0].Should().Be(42.0);

        second.Dispose();
        await WaitUntilAsync(async () => !await RegistryContainsAsync(id));

        (await RegistryContainsAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task DoubleRelease_IsANoOp_NotAnError()
    {
        // Directly exercises ReleaseRefAsync twice for the same id -
        // the second call must not throw (docs/progress.md: double
        // release is a no-op by design, not a protocol error).
        RHandle handle = await _supervisor.SetObjAsync(
            RValue.OfDouble(new double[] { 1.0 }), TestContext.Current.CancellationToken);
        long id = handle.Id;

        await _supervisor.ReleaseRefAsync(id, TestContext.Current.CancellationToken);

        Func<Task> secondRelease = () => _supervisor.ReleaseRefAsync(id, TestContext.Current.CancellationToken);
        await secondRelease.Should().NotThrowAsync();

        _supervisor.State.Should().Be(SupervisorState.Ready);
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
        RHandle handleFromFirstSession = await _supervisor.SetObjAsync(
            RValue.OfDouble(new double[] { 1.0 }), TestContext.Current.CancellationToken);

        using var secondSupervisor = new ProcessSupervisor(
            new RWireOptions { WorkerScriptPath = RWireProcessFixture.WorkerScriptPath });
        await secondSupervisor.StartAsync(TestContext.Current.CancellationToken);

        Func<Task> act = () => secondSupervisor.GetObjAsync(handleFromFirstSession, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

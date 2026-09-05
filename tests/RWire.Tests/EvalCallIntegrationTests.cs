using Xunit;

namespace RWire.Tests;

/// <summary>
/// Phase 2 exit-criteria integration tests (docs/phases/phase-2-atomic-types.md):
/// EVAL/CALL round-trips of simple atomic results, and the
/// non-fatal-error path (a deliberately erroring EVAL/CALL returns
/// ERROR without killing the connection). Requires a real R
/// installation with Rscript on PATH.
/// </summary>
public class EvalCallIntegrationTests : IAsyncLifetime
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

    [Fact]
    public async Task EvalAsync_SimpleArithmeticExpression_ReturnsCorrectDouble()
    {
        RValue result = await _supervisor.EvalAsync("21 * 2");

        Assert.Equal(RTypeTag.Double, result.TypeTag);
        Assert.Equal(42.0, result.DoubleValues![0]);
    }

    [Fact]
    public void Eval_Sync_SimpleArithmeticExpression_ReturnsCorrectDouble()
    {
        RValue result = _supervisor.Eval("6 * 7");

        Assert.Equal(RTypeTag.Double, result.TypeTag);
        Assert.Equal(42.0, result.DoubleValues![0]);
    }

    [Fact]
    public async Task EvalAsync_CharacterVector_RoundTrips()
    {
        RValue result = await _supervisor.EvalAsync("c('a', NA, 'c')");

        Assert.Equal(RTypeTag.Character, result.TypeTag);
        Assert.Equal(new string?[] { "a", null, "c" }, result.CharacterValues);
    }

    [Fact]
    public async Task EvalAsync_FactorExpression_RoundTripsWithLevels()
    {
        RValue result = await _supervisor.EvalAsync(
            "factor(c('low', 'high', 'low'), levels = c('low', 'medium', 'high'))");

        Assert.Contains("factor", result.Class);
        Assert.Equal(
            new string?[] { "low", "medium", "high" },
            result.Attributes!["levels"].CharacterValues);
    }

    [Fact]
    public async Task CallAsync_SumFunction_ReturnsCorrectResult()
    {
        RValue result = await _supervisor.CallAsync(
            "sum", new RCallArgument[] { RValue.OfDouble(new double[] { 1, 2, 3, 4 }) });

        Assert.Equal(10.0, result.DoubleValues![0]);
    }

    [Fact]
    public async Task CallAsync_WithHandleArgument_ResolvesWithoutDataCrossingWireTwice()
    {
        using RHandle handle = await _supervisor.SetObjAsync(RValue.OfDouble(new double[] { 5, 10, 15 }));

        RValue result = await _supervisor.CallAsync("sum", new RCallArgument[] { handle });

        Assert.Equal(30.0, result.DoubleValues![0]);
    }

    [Fact]
    public async Task EvalAsync_ErroringExpression_ThrowsRErrorException_WithoutFaultingConnection()
    {
        await Assert.ThrowsAsync<RErrorException>(() => _supervisor.EvalAsync("stop('deliberate test error')"));

        // The non-fatal path: State must still be usable afterward.
        Assert.Equal(SupervisorState.Ready, _supervisor.State);

        // And the connection should still work for a subsequent call.
        RValue result = await _supervisor.EvalAsync("1 + 1");
        Assert.Equal(2.0, result.DoubleValues![0]);
    }

    [Fact]
    public async Task CallAsync_UnknownFunction_ThrowsRErrorException_WithoutFaultingConnection()
    {
        await Assert.ThrowsAsync<RErrorException>(
            () => _supervisor.CallAsync("this_function_does_not_exist", Array.Empty<RCallArgument>()));

        Assert.Equal(SupervisorState.Ready, _supervisor.State);
    }
}

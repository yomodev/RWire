using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Phase 2/3 EVAL/CALL integration tests. Uses the shared
/// RWireProcessFixture (one R process for the whole class) rather than
/// starting a fresh RScript.exe per test - none of these tests mutate
/// the supervisor's lifecycle itself, only make ordinary calls against
/// it, so sharing is safe and meaningfully faster. Requires a real R
/// installation with Rscript on PATH.
/// </summary>
[Collection(nameof(RWireProcessCollection))]
public class EvalCallIntegrationTests
{
    private readonly ProcessSupervisor _supervisor;

    public EvalCallIntegrationTests(RWireProcessFixture fixture)
    {
        _supervisor = fixture.Supervisor;
    }

    [Fact]
    public async Task EvalAsync_SimpleArithmeticExpression_ReturnsCorrectDouble()
    {
        RValue result = await _supervisor.EvalAsync("21 * 2", TestContext.Current.CancellationToken);

        result.TypeTag.Should().Be(RTypeTag.Double);
        result.DoubleValues![0].Should().Be(42.0);
    }

    [Fact]
    public void Eval_Sync_SimpleArithmeticExpression_ReturnsCorrectDouble()
    {
        RValue result = _supervisor.Eval("6 * 7");

        result.TypeTag.Should().Be(RTypeTag.Double);
        result.DoubleValues![0].Should().Be(42.0);
    }

    [Fact]
    public async Task EvalAsync_CharacterVector_RoundTrips()
    {
        RValue result = await _supervisor.EvalAsync("c('a', NA, 'c')", TestContext.Current.CancellationToken);

        result.TypeTag.Should().Be(RTypeTag.Character);
        result.CharacterValues.Should().Equal("a", null, "c");
    }

    [Fact]
    public async Task EvalAsync_FactorExpression_RoundTripsWithLevels()
    {
        RValue result = await _supervisor.EvalAsync(
            "factor(c('low', 'high', 'low'), levels = c('low', 'medium', 'high'))",
            TestContext.Current.CancellationToken);

        result.Class.Should().Contain("factor");
        result.Attributes!["levels"].CharacterValues.Should().Equal("low", "medium", "high");
    }

    [Fact]
    public async Task CallAsync_SumFunction_ReturnsCorrectResult()
    {
        RValue result = await _supervisor.CallAsync(
            "sum",
            new RCallArgument[] { RValue.OfDouble(new double[] { 1, 2, 3, 4 }) },
            TestContext.Current.CancellationToken);

        result.DoubleValues![0].Should().Be(10.0);
    }

    [Fact]
    public async Task CallAsync_WithHandleArgument_ResolvesWithoutDataCrossingWireTwice()
    {
        using RHandle handle = await _supervisor.SetObjAsync(
            RValue.OfDouble(new double[] { 5, 10, 15 }), TestContext.Current.CancellationToken);

        RValue result = await _supervisor.CallAsync(
            "sum", new RCallArgument[] { handle }, TestContext.Current.CancellationToken);

        result.DoubleValues![0].Should().Be(30.0);
    }

    [Fact]
    public async Task EvalAsync_ErroringExpression_ThrowsRErrorException_WithoutFaultingConnection()
    {
        Func<Task> act = () => _supervisor.EvalAsync(
            "stop('deliberate test error')", TestContext.Current.CancellationToken);

        RErrorException error = (await act.Should().ThrowAsync<RErrorException>()).Which;
        error.Message.Should().Be("deliberate test error");
        error.Classes.Should().Contain("simpleError");

        // The non-fatal path: State must still be usable afterward.
        _supervisor.State.Should().Be(SupervisorState.Ready);

        // And the connection should still work for a subsequent call.
        RValue result = await _supervisor.EvalAsync("1 + 1", TestContext.Current.CancellationToken);
        result.DoubleValues![0].Should().Be(2.0);
    }

    [Fact]
    public async Task CallAsync_UnknownFunction_ThrowsRErrorException_WithoutFaultingConnection()
    {
        Func<Task> act = () => _supervisor.CallAsync(
            "this_function_does_not_exist", Array.Empty<RCallArgument>(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<RErrorException>();

        _supervisor.State.Should().Be(SupervisorState.Ready);
    }
}

using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Phase 5 cold-path integration tests (docs/spec.md section 7):
/// R values with no dedicated mapping (environments, closures) come
/// back as an opaque SerializedBlob rather than an error, and can be
/// round-tripped back to R (via SET_OBJ + CALL) where R's
/// unserialize() reconstructs the original object correctly. Requires
/// a real R installation with Rscript on PATH.
/// </summary>
[Collection(nameof(RWireProcessCollection))]
public class ColdPathIntegrationTests
{
    private readonly ProcessSupervisor _supervisor;

    public ColdPathIntegrationTests(RWireProcessFixture fixture)
    {
        _supervisor = fixture.Supervisor;
    }

    [Fact]
    public async Task EvalAsync_Environment_ReturnsAsSerializedBlob()
    {
        RValue result = await _supervisor.EvalAsync(
            "{ e <- new.env(); e$x <- 42; e }", TestContext.Current.CancellationToken);

        result.TypeTag.Should().Be(RTypeTag.SerializedBlob);
        result.SerializedBytes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EvalAsync_Closure_ReturnsAsSerializedBlob()
    {
        RValue result = await _supervisor.EvalAsync(
            "function(x) x + 1", TestContext.Current.CancellationToken);

        result.TypeTag.Should().Be(RTypeTag.SerializedBlob);
        result.SerializedBytes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SerializedBlob_RoundTripsThroughR_ViaSetObjAndCall()
    {
        // A closure this side can't inspect, but R can - store it via
        // SET_OBJ (round-tripping the opaque bytes unexamined), then
        // CALL it via do.call, proving R's unserialize() reconstructed
        // a genuinely working function object, not just matching bytes.
        RValue closure = await _supervisor.EvalAsync(
            "function(x) x * 10", TestContext.Current.CancellationToken);

        using RHandle handle = await _supervisor.SetObjAsync(closure, TestContext.Current.CancellationToken);

        RValue result = await _supervisor.CallAsync(
            "do.call",
            new RCallArgument[] { handle, RValue.OfList(new[] { RValue.OfDouble(new double[] { 4 }) }) },
            TestContext.Current.CancellationToken);

        result.DoubleValues![0].Should().Be(40.0);
    }

    [Fact]
    public async Task EvalAsync_ListContainingAClosure_TreatsOnlyThatElementAsSerializedBlob()
    {
        // Confirms the cold path composes with the existing List
        // handling - a list is still encoded via RTAG_LIST (recursing
        // per-element), and only the element R can't map falls back
        // to serialize(), rather than the whole list becoming opaque.
        RValue result = await _supervisor.EvalAsync(
            "list(1:3, function(x) x)", TestContext.Current.CancellationToken);

        result.TypeTag.Should().Be(RTypeTag.List);
        result.ListValues!.Should().HaveCount(2);
        result.ListValues[0].TypeTag.Should().Be(RTypeTag.Integer);
        result.ListValues[1].TypeTag.Should().Be(RTypeTag.SerializedBlob);
    }
}

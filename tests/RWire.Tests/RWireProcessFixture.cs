using Xunit;

namespace RWire.Tests;

/// <summary>
/// Starts one ProcessSupervisor and shares it across every test class
/// in the RWireProcessCollection, instead of every test spawning its
/// own RScript.exe (slow - process launch + handshake dominates test
/// run time otherwise). Only safe for tests that don't need an
/// isolated process: crash, dispose, and restart/session-mismatch
/// scenarios still create their own ProcessSupervisor instances (see
/// ProcessSupervisorTests.cs), since those tests need to control the
/// process's lifetime directly.
/// </summary>
public sealed class RWireProcessFixture : IAsyncLifetime
{
    public ProcessSupervisor Supervisor { get; private set; } = null!;

    public static string WorkerScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "r", "worker.R");

    public async ValueTask InitializeAsync()
    {
        Supervisor = new ProcessSupervisor(new RWireOptions { WorkerScriptPath = WorkerScriptPath });
        await Supervisor.StartAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Supervisor.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// xUnit collection tying test classes to RWireProcessFixture - apply
/// [Collection(nameof(RWireProcessCollection))] to a test class and
/// take an RWireProcessFixture constructor parameter to use the shared
/// process instead of starting a new one.
/// </summary>
[CollectionDefinition(nameof(RWireProcessCollection))]
public sealed class RWireProcessCollection : ICollectionFixture<RWireProcessFixture>
{
}

namespace RWire;

/// <summary>
/// The call surface a host application actually depends on to drive
/// an R session: status/diagnostics, lifecycle, and the five
/// Eval/Call/SetObj/GetObj/CreateRef operations (sync and async).
/// Exists so application code can depend on this interface instead of
/// the concrete <see cref="ProcessSupervisor"/> and substitute a test
/// double in its own unit tests.
///
/// This is deliberately not a full abstraction over
/// <see cref="ProcessSupervisor"/>'s internals: <see cref="RHandle"/>
/// still calls back into the concrete class (via an internal
/// release method) regardless of which type a caller holds its
/// supervisor through, so a handle obtained from a mock implementation
/// of this interface cannot be a real, disposable RHandle. Any test
/// double implementing this interface needs its own approach to
/// standing in for RHandle-returning members (SetObj/SetObjAsync/
/// CreateRef/CreateRefAsync) - see docs/phases/phase-8-plan.md,
/// "IProcessSupervisor design notes," for the reasoning behind this
/// boundary and when it would be worth revisiting.
/// </summary>
public interface IProcessSupervisor : IDisposable
{
    /// <inheritdoc cref="ProcessSupervisor.State"/>
    SupervisorState State { get; }

    /// <inheritdoc cref="ProcessSupervisor.SessionId"/>
    ulong SessionId { get; }

    /// <inheritdoc cref="ProcessSupervisor.Port"/>
    int Port { get; }

    /// <inheritdoc cref="ProcessSupervisor.ChannelArgument"/>
    string ChannelArgument { get; }

    /// <inheritdoc cref="ProcessSupervisor.RVersion"/>
    string? RVersion { get; }

    /// <inheritdoc cref="ProcessSupervisor.ExitCode"/>
    int? ExitCode { get; }

    /// <inheritdoc cref="ProcessSupervisor.RestartCount"/>
    int RestartCount { get; }

    /// <inheritdoc cref="ProcessSupervisor.IsPermanentlyFailed"/>
    bool IsPermanentlyFailed { get; }

    /// <inheritdoc cref="ProcessSupervisor.LastFault"/>
    Exception? LastFault { get; }

    /// <inheritdoc cref="ProcessSupervisor.RecentDiagnosticOutput"/>
    IReadOnlyList<string> RecentDiagnosticOutput { get; }

    /// <inheritdoc cref="ProcessSupervisor.DiagnosticOutput"/>
    event Action<string, bool>? DiagnosticOutput;

    /// <inheritdoc cref="ProcessSupervisor.StartAsync"/>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ProcessSupervisor.Eval"/>
    RValue Eval(string expression);

    /// <inheritdoc cref="ProcessSupervisor.EvalAsync"/>
    Task<RValue> EvalAsync(string expression, CancellationToken ct = default);

    /// <inheritdoc cref="ProcessSupervisor.Call"/>
    RValue Call(string functionName, IReadOnlyList<RCallArgument> arguments);

    /// <inheritdoc cref="ProcessSupervisor.CallAsync"/>
    Task<RValue> CallAsync(string functionName, IReadOnlyList<RCallArgument> arguments, CancellationToken ct = default);

    /// <inheritdoc cref="ProcessSupervisor.SetObj"/>
    RHandle SetObj(RValue value);

    /// <inheritdoc cref="ProcessSupervisor.SetObjAsync"/>
    Task<RHandle> SetObjAsync(RValue value, CancellationToken ct = default);

    /// <inheritdoc cref="ProcessSupervisor.GetObj"/>
    RValue GetObj(RHandle handle);

    /// <inheritdoc cref="ProcessSupervisor.GetObjAsync"/>
    Task<RValue> GetObjAsync(RHandle handle, CancellationToken ct = default);

    /// <inheritdoc cref="ProcessSupervisor.CreateRef"/>
    RHandle CreateRef(RHandle handle);

    /// <inheritdoc cref="ProcessSupervisor.CreateRefAsync"/>
    Task<RHandle> CreateRefAsync(RHandle handle, CancellationToken ct = default);
}

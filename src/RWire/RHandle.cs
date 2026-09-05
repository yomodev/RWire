namespace RWire;

/// <summary>
/// An opaque reference to an object held in the R worker's object
/// registry (docs/spec.md section 8). Dispose (or letting the
/// finalizer fire as a last-resort safety net) releases the
/// R-side reference; the actual refcounting happens entirely on the
/// R side, so this class is a thin proxy, not the source of truth.
///
/// A handle is scoped to the R process session it was created in
/// (ProcessSupervisor.SessionId) - using it against a different
/// session (e.g. after a future Phase 6 restart) throws rather than
/// silently addressing the wrong process's registry.
/// </summary>
public sealed class RHandle : IDisposable
{
    private readonly ProcessSupervisor _owner;
    private readonly ulong _sessionId;
    private long _id;
    private bool _disposed;

    internal RHandle(ProcessSupervisor owner, ulong sessionId, long id)
    {
        _owner = owner;
        _sessionId = sessionId;
        _id = id;
    }

    internal long Id => _disposed
        ? throw new ObjectDisposedException(nameof(RHandle))
        : _id;

    internal ulong SessionId => _sessionId;

    /// <summary>
    /// Releases the R-side reference. Best-effort: failures (e.g. the
    /// connection is already gone) are swallowed rather than thrown,
    /// since Dispose must never throw and a handle whose worker
    /// process is already gone has nothing left to release anyway.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
        _owner.ReleaseHandleBestEffort(_sessionId, _id);
    }

    ~RHandle()
    {
        // Last-resort safety net only - Dispose() is the primary path
        // and GC finalization timing is not something to rely on
        // (docs/spec.md section 8).
        if (!_disposed)
        {
            _owner.ReleaseHandleBestEffort(_sessionId, _id);
        }
    }
}

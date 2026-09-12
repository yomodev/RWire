namespace RWire;

/// <summary>
/// Bounded ring buffer of recent stdout/stderr lines from the R
/// worker process, plus the per-line notification ProcessSupervisor
/// re-exposes as its public DiagnosticOutput event (docs/spec.md
/// section 11, Phase 6: "log correlation with the failure event").
///
/// Extracted out of ProcessSupervisor (Phase 8): this is a fully
/// self-contained unit - its own lock, its own state, no dependency
/// on process/connection/session state - unlike most of
/// ProcessSupervisor's other responsibilities. See
/// docs/phases/processsupervisor-decomposition.md for why this was
/// safe to pull out mechanically while the rest of the class was not.
/// </summary>
internal sealed class DiagnosticsBuffer
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Queue<string> _lines = new();

    public DiagnosticsBuffer(int capacity)
    {
        _capacity = capacity;
    }

    /// <summary>Raised for every line recorded; isError is true for stderr lines.</summary>
    public event Action<string, bool>? LineRecorded;

    /// <summary>The most recent lines recorded, bounded by the capacity passed to the constructor.</summary>
    public IReadOnlyList<string> Recent
    {
        get
        {
            lock (_lock)
            {
                return _lines.ToArray();
            }
        }
    }

    /// <summary>
    /// Reads lines from reader and records each (raising
    /// LineRecorded), until EOF - normally because the process that
    /// owns the stream exited. Intended to run as a background task
    /// started right after the process launches, so early output -
    /// including from a process that fails almost immediately - is
    /// captured well before any caller gets around to checking
    /// Recent.
    /// </summary>
    public async Task PumpAsync(TextReader reader, bool isError)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > _capacity)
                {
                    _lines.Dequeue();
                }
            }

            LineRecorded?.Invoke(line, isError);
        }
    }
}

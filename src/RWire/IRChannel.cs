namespace RWire;

/// <summary>
/// Transport abstraction the frame codec is written against (see
/// docs/spec.md section 2.1). Both synchronous and asynchronous
/// members are implemented natively by each concrete channel - never
/// derived from one another - so the sync path avoids async
/// state-machine overhead and the async path never risks a
/// sync-over-async deadlock.
/// </summary>
public interface IRChannel : IDisposable
{
    /// <summary>Blocking read. Returns 0 only on a graceful close (EOF).</summary>
    int Read(Span<byte> buffer);

    /// <summary>Blocking write of the full buffer.</summary>
    void Write(ReadOnlySpan<byte> buffer);

    /// <summary>Async read. Returns 0 only on a graceful close (EOF).</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Async write of the full buffer.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
}

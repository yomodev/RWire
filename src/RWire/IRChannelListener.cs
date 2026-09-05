namespace RWire;

/// <summary>
/// Abstracts "how the data channel is established" away from
/// ProcessSupervisor: binding a listenable endpoint, exposing whatever
/// the R worker needs to connect back to it, and accepting that
/// connection as an IRChannel. ProcessSupervisor depends on this
/// interface rather than constructing a TcpListener/TcpClient
/// directly, so:
///   - tests can substitute a fake/in-memory listener without a real
///     socket or R process where that's all that's needed, and
///   - a future channel (named pipe, memory-mapped file) plugs in as
///     a new IRChannelListener implementation without touching
///     ProcessSupervisor's logic at all (docs/spec.md section 2.1's
///     channel-agnostic goal, now applied one level up from IRChannel
///     itself).
/// </summary>
public interface IRChannelListener : IDisposable
{
    /// <summary>The port (or other connection info) the R worker should be told to connect back to.</summary>
    int Port { get; }

    /// <summary>Waits for and accepts the R worker's connect-back, returning the resulting channel.</summary>
    Task<IRChannel> AcceptAsync(CancellationToken cancellationToken = default);
}

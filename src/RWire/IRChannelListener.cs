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
///   - a future channel (named pipe, memory-mapped file, a remote TCP
///     endpoint) plugs in as a new IRChannelListener implementation
///     without touching ProcessSupervisor's logic at all (docs/spec.md
///     section 2.1's channel-agnostic goal, now applied one level up
///     from IRChannel itself).
/// </summary>
public interface IRChannelListener : IDisposable
{
    /// <summary>
    /// Whatever the R worker's --endpoint= argument needs to be to
    /// connect back to this listener - a port number as a string for
    /// TCP, a pipe name for a future named-pipe implementation, or
    /// "host:port" for a future remote-TCP implementation. Kept as a
    /// plain string rather than something TCP-specific like an int
    /// port, precisely so non-TCP channels aren't forced to invent a
    /// fake port number just to satisfy this interface.
    /// </summary>
    string ChannelArgument { get; }

    /// <summary>Waits for and accepts the R worker's connect-back, returning the resulting channel.</summary>
    Task<IRChannel> AcceptAsync(CancellationToken cancellationToken = default);
}

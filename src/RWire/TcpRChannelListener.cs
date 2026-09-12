using System.Net;
using System.Net.Sockets;

namespace RWire;

/// <summary>
/// The v1 (and so far only) IRChannelListener implementation: a
/// loopback TcpListener bound to an ephemeral port. This is what
/// ProcessSupervisor uses by default; pass a different
/// IRChannelListener to its constructor to use another transport or a
/// test fake.
/// </summary>
public sealed class TcpRChannelListener : IRChannelListener
{
    private readonly TcpListener _listener;
    private bool _disposed;

    public TcpRChannelListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
    }

    /// <summary>TCP-specific convenience accessor - not part of IRChannelListener itself, since a non-TCP channel (a named pipe, say) has no port at all. Prefer ChannelArgument for anything channel-agnostic.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The port, as a string - what gets passed to the R worker's --endpoint= argument.</summary>
    public string ChannelArgument => Port.ToString();

    public async Task<IRChannel> AcceptAsync(CancellationToken cancellationToken = default)
    {
        TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

        // Performance tuning (docs/phases/phase-7-performance-hardening.md):
        // - NoDelay disables Nagle's algorithm, which otherwise adds up
        //   to ~40ms of latency batching small writes together - RWire's
        //   protocol is a tight request/response loop of exactly the
        //   small-frequent-message shape Nagle's algorithm penalizes
        //   most, so this matters far more here than for a typical bulk
        //   HTTP-style connection.
        // - Larger send/receive buffers reduce the number of kernel
        //   round trips needed for a large TABLE payload; 1MB is a
        //   reasonable default for a loopback connection carrying
        //   occasional large transfers, well within typical OS limits.
        client.NoDelay = true;
        client.SendBufferSize = 1024 * 1024;
        client.ReceiveBufferSize = 1024 * 1024;

        return new SocketRChannel(client);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Best-effort teardown.
        }
    }
}

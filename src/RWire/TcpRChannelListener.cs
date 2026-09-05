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

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<IRChannel> AcceptAsync(CancellationToken cancellationToken = default)
    {
        TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
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

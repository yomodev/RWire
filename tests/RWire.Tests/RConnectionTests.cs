using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Exercises RConnection (both sync and async paths) over a real
/// loopback TCP socket pair, without launching any R process - these
/// are still unit-level tests of the C# protocol layer per docs/spec.md
/// section 12.1, just using a real socket instead of an in-memory fake
/// since IRChannel's contract is defined in terms of a real stream.
/// </summary>
public class RConnectionTests : IAsyncLifetime
{
    private TcpListener _listener = null!;
    private TcpClient _clientSide = null!;
    private TcpClient _serverSide = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = _listener.AcceptTcpClientAsync();
        _clientSide = new TcpClient();
        await _clientSide.ConnectAsync(IPAddress.Loopback, port);
        _serverSide = await acceptTask;
    }

    public ValueTask DisposeAsync()
    {
        _clientSide.Dispose();
        _serverSide.Dispose();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private (RConnection Client, RConnection Server) MakeConnectionPair()
    {
        var client = new RConnection(new SocketRChannel(_clientSide));
        var server = new RConnection(new SocketRChannel(_serverSide));
        return (client, server);
    }

    [Fact]
    public void Send_ThenReceive_Sync_RoundTripsPayload()
    {
        (RConnection client, RConnection server) = MakeConnectionPair();

        byte[] payload = Encoding.UTF8.GetBytes("hello from RWire");
        client.Send(MsgType.Eval, correlationId: 3, payload);

        using Frame received = server.Receive();

        received.MsgType.Should().Be(MsgType.Eval);
        received.CorrelationId.Should().Be(3u);
        received.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task SendAsync_ThenReceiveAsync_RoundTripsPayload()
    {
        (RConnection client, RConnection server) = MakeConnectionPair();

        byte[] payload = Encoding.UTF8.GetBytes("async round trip");
        await client.SendAsync(MsgType.Call, correlationId: 9, payload);

        using Frame received = await server.ReceiveAsync();

        received.MsgType.Should().Be(MsgType.Call);
        received.CorrelationId.Should().Be(9u);
        received.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Send_ZeroLengthPayload_RoundTrips()
    {
        (RConnection client, RConnection server) = MakeConnectionPair();

        client.Send(MsgType.Ping, correlationId: 1, ReadOnlySpan<byte>.Empty);

        using Frame received = server.Receive();

        received.MsgType.Should().Be(MsgType.Ping);
        received.Payload.Length.Should().Be(0);
    }

    [Fact]
    public void Receive_AfterChannelClosed_ThrowsEndOfStream()
    {
        (RConnection client, RConnection server) = MakeConnectionPair();

        client.Dispose(); // closes the underlying socket

        Action act = () => server.Receive();

        act.Should().Throw<EndOfStreamException>();
    }

    [Fact]
    public void NextCorrelationId_IncrementsAndNeverReturnsZero()
    {
        var connection = new RConnection(new SocketRChannel(_clientSide));

        uint first = connection.NextCorrelationId();
        uint second = connection.NextCorrelationId();

        first.Should().NotBe(0u);
        second.Should().Be(first + 1);
    }
}

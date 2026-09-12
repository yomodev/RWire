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

        // AcceptTcpClientAsync(CancellationToken) returns ValueTask<TcpClient>
        // (the no-argument overload returns Task<TcpClient> instead - an
        // easy mismatch, which is exactly what broke the build here
        // before). `var` below sidesteps needing to know whether
        // ConnectAsync's return type is Task or ValueTask too - either
        // way it's awaited after the accept has been kicked off, so
        // both sides of the handshake are in flight concurrently.
        ValueTask<TcpClient> acceptTask = _listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        _clientSide = new TcpClient();
        var connectTask = _clientSide.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);

        _serverSide = await acceptTask;
        await connectTask;
    }

    public ValueTask DisposeAsync()
    {
        _clientSide.Dispose();
        _serverSide.Dispose();
        _listener.Stop();
        GC.SuppressFinalize(this);
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
        await client.SendAsync(MsgType.Call, correlationId: 9, payload, TestContext.Current.CancellationToken);

        using Frame received = await server.ReceiveAsync(TestContext.Current.CancellationToken);

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
    public void Send_LargePayload_RoundTrips()
    {
        // Phase 7: Send/SendAsync write the header and payload as two
        // separate channel writes (avoiding a copy of the payload into
        // a combined buffer) rather than one - this specifically
        // checks that a payload large enough to span multiple TCP
        // segments still round-trips correctly, i.e. that the
        // receiver's ReadExact loop correctly reassembles it
        // regardless of how many underlying writes/segments composed
        // the stream.
        (RConnection client, RConnection server) = MakeConnectionPair();

        byte[] payload = new byte[5 * 1024 * 1024]; // 5 MB - well beyond a single typical TCP segment/buffer
        new Random(7).NextBytes(payload);

        // Send runs on a background thread concurrently with Receive()
        // below: Send is synchronous/blocking, and a payload this size
        // can exceed the OS socket send buffer - if nothing were
        // draining the socket while Send blocked, this would deadlock
        // rather than complete.
        Task sendTask = Task.Run(() => client.Send(MsgType.Result, correlationId: 42, payload));

        using Frame received = server.Receive();
        sendTask.Wait(TestContext.Current.CancellationToken);

        received.MsgType.Should().Be(MsgType.Result);
        received.CorrelationId.Should().Be(42u);
        received.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task SendAsync_LargePayload_RoundTrips()
    {
        (RConnection client, RConnection server) = MakeConnectionPair();

        byte[] payload = new byte[5 * 1024 * 1024];
        new Random(11).NextBytes(payload);

        // Same concurrency requirement as the sync test above - run
        // both sides at once rather than awaiting the send fully
        // before starting the receive.
        Task sendTask = client.SendAsync(
            MsgType.Result, correlationId: 43, payload, TestContext.Current.CancellationToken).AsTask();
        Task<Frame> receiveTask = server.ReceiveAsync(TestContext.Current.CancellationToken).AsTask();

        await Task.WhenAll(sendTask, receiveTask);

        using Frame received = await receiveTask;
        received.MsgType.Should().Be(MsgType.Result);
        received.CorrelationId.Should().Be(43u);
        received.Payload.ToArray().Should().Equal(payload);
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

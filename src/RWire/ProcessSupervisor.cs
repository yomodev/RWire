using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RWire;

/// <summary>
/// Configuration for launching the R worker process.
/// </summary>
public sealed class RWireOptions
{
    /// <summary>
    /// Path to the Rscript executable. Defaults to "Rscript", which
    /// resolves via PATH on most installations.
    /// </summary>
    public string RScriptPath { get; init; } = "Rscript";

    /// <summary>
    /// Path to the worker.R script (r/worker.R in the repo).
    /// </summary>
    public required string WorkerScriptPath { get; init; }

    /// <summary>
    /// How long to wait for the R process to connect back and send a
    /// valid HELLO frame before treating startup as failed.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for the R process to exit on its own after a
    /// SHUTDOWN frame is sent during Dispose, before force-killing it.
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often to send a PING while the connection is idle (State == Ready).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a PONG before treating the connection as Faulted.</summary>
    public TimeSpan HeartbeatResponseTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Phase 1 subset of the full lifecycle state machine (docs/spec.md
/// section 3.2). Restarting/backoff arrive in Phase 6.
/// </summary>
public enum SupervisorState
{
    NotStarted,
    Starting,
    Ready,
    Faulted,
    Disposed,
}

/// <summary>
/// Launches the R worker process, performs the real HELLO handshake
/// over the frame protocol, keeps the connection alive with a
/// PING/PONG heartbeat, and shuts the worker down gracefully (or
/// force-kills it) on Dispose.
///
/// Does not yet implement reference counting (Phase 3), EVAL/CALL
/// (Phase 2), the full restart-on-crash state machine (Phase 6), or
/// TABLE transfer (Phase 4).
///
/// See docs/phases/phase-1-channel-protocol.md for the checklist this
/// class implements against.
/// </summary>
public sealed class ProcessSupervisor : IDisposable
{
    private readonly RWireOptions _options;
    private readonly Process _process;
    private readonly TcpListener _listener;
    private readonly string _token;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private RConnection? _connection;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatLoopTask;
    private bool _disposed;

    public SupervisorState State { get; private set; } = SupervisorState.NotStarted;

    /// <summary>The ephemeral loopback port the R process was told to connect back to.</summary>
    public int Port { get; }

    /// <summary>The R version string reported in the HELLO frame, populated after StartAsync completes.</summary>
    public string? RVersion { get; private set; }

    /// <summary>
    /// Raised for every line the R process writes to stdout or
    /// stderr. isError is true for stderr lines. Phase 6 adds
    /// fatal-signature scanning as a secondary crash-detection signal
    /// on top of this.
    /// </summary>
    public event Action<string, bool>? DiagnosticOutput;

    /// <summary>Exposed for test purposes only (see AssemblyInfo.cs's InternalsVisibleTo).</summary>
    internal Process ProcessForTesting => _process;

    public ProcessSupervisor(RWireOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _token = Guid.NewGuid().ToString("N");

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.RScriptPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(_options.WorkerScriptPath);
        startInfo.ArgumentList.Add("--channel=socket");
        startInfo.ArgumentList.Add($"--port={Port}");
        startInfo.ArgumentList.Add($"--token={_token}");

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                DiagnosticOutput?.Invoke(e.Data, false);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                DiagnosticOutput?.Invoke(e.Data, true);
            }
        };
        _process.Exited += (_, _) =>
        {
            if (State is SupervisorState.Starting or SupervisorState.Ready)
            {
                State = SupervisorState.Faulted;
            }
        };
    }

    /// <summary>
    /// Starts the R process, waits for it to connect back, validates
    /// the HELLO handshake, and starts the heartbeat loop. Throws if
    /// the process fails to launch, never connects/sends HELLO within
    /// HandshakeTimeout, or the handshake token doesn't match.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State != SupervisorState.NotStarted)
        {
            throw new InvalidOperationException(
                $"StartAsync can only be called once, from NotStarted (current state: {State}).");
        }

        State = SupervisorState.Starting;

        bool started;
        try
        {
            started = _process.Start();
        }
        catch (Exception ex)
        {
            State = SupervisorState.Faulted;
            throw new InvalidOperationException(
                $"Failed to start '{_options.RScriptPath}'. Is Rscript on PATH?", ex);
        }

        if (!started)
        {
            State = SupervisorState.Faulted;
            throw new InvalidOperationException("Process.Start returned false.");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(_options.HandshakeTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        TcpClient client;
        try
        {
            client = await _listener.AcceptTcpClientAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            State = SupervisorState.Faulted;
            throw new TimeoutException(
                $"Timed out after {_options.HandshakeTimeout} waiting for the R worker " +
                "to connect back. Check stderr via DiagnosticOutput for R-side errors.");
        }

        _connection = new RConnection(new SocketRChannel(client));

        try
        {
            await ReceiveAndValidateHelloAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            State = SupervisorState.Faulted;
            throw new TimeoutException(
                $"Timed out after {_options.HandshakeTimeout} waiting for the HELLO frame.");
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }

        State = SupervisorState.Ready;

        _heartbeatCts = new CancellationTokenSource();
        _heartbeatLoopTask = HeartbeatLoopAsync(_heartbeatCts.Token);
    }

    private async Task ReceiveAndValidateHelloAsync(CancellationToken ct)
    {
        using Frame frame = await _connection!.ReceiveAsync(ct).ConfigureAwait(false);

        if (frame.MsgType != MsgType.Hello)
        {
            throw new InvalidOperationException(
                $"Expected HELLO as the first frame, got {frame.MsgType}.");
        }

        ReadOnlySpan<byte> payload = frame.Payload.Span;
        int offset = 0;
        string receivedToken = ReadLengthPrefixedString(payload, ref offset);
        string rVersion = ReadLengthPrefixedString(payload, ref offset);

        if (receivedToken != _token)
        {
            throw new InvalidOperationException(
                "Handshake token mismatch - a stray or previous R process may have " +
                "connected to this listener.");
        }

        RVersion = rVersion;
    }

    private static string ReadLengthPrefixedString(ReadOnlySpan<byte> buffer, ref int offset)
    {
        if (offset + 4 > buffer.Length)
        {
            throw new InvalidDataException("HELLO payload truncated while reading a string length.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;

        if (length < 0 || offset + length > buffer.Length)
        {
            throw new InvalidDataException("HELLO payload truncated while reading string bytes.");
        }

        string value = Encoding.UTF8.GetString(buffer.Slice(offset, length));
        offset += length;
        return value;
    }

    /// <summary>
    /// Sends a PING and awaits a PONG on the configured interval while
    /// State == Ready. Skips a tick (rather than blocking) if the
    /// connection is already in use by an application call, since a
    /// call in flight is itself evidence the connection is alive - see
    /// docs/spec.md section 3.3 and the non-goal of concurrent
    /// pipelining in section 1.2.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (State != SupervisorState.Ready)
            {
                continue;
            }

            if (!await _connectionLock.WaitAsync(0, ct).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                uint correlationId = _connection!.NextCorrelationId();
                await _connection.SendAsync(MsgType.Ping, correlationId, ReadOnlyMemory<byte>.Empty, ct)
                    .ConfigureAwait(false);

                using var responseTimeoutCts = new CancellationTokenSource(_options.HeartbeatResponseTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, responseTimeoutCts.Token);

                using Frame response = await _connection.ReceiveAsync(linked.Token).ConfigureAwait(false);
                if (response.MsgType != MsgType.Pong)
                {
                    throw new InvalidOperationException(
                        $"Expected PONG in response to a heartbeat PING, got {response.MsgType}.");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Heartbeat-specific timeout (responseTimeoutCts), not
                // supervisor shutdown - the connection is unresponsive.
                State = SupervisorState.Faulted;
            }
            catch (OperationCanceledException)
            {
                // Supervisor is disposing - exit the loop quietly.
                return;
            }
            catch
            {
                State = SupervisorState.Faulted;
            }
            finally
            {
                _connectionLock.Release();
            }
        }
    }

    /// <summary>
    /// Sends a graceful SHUTDOWN, closes the connection, and waits up
    /// to ShutdownGracePeriod for the process to exit before
    /// force-killing it. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        State = SupervisorState.Disposed;

        _heartbeatCts?.Cancel();

        try
        {
            if (_connection is not null)
            {
                uint correlationId = _connection.NextCorrelationId();
                _connection.Send(MsgType.Shutdown, correlationId, ReadOnlySpan<byte>.Empty);
            }
        }
        catch
        {
            // Best-effort - the WaitForExit/Kill fallback below still applies.
        }

        try
        {
            _connection?.Dispose();
        }
        catch
        {
            // Best-effort teardown.
        }

        try
        {
            _listener.Stop();
        }
        catch
        {
            // Best-effort teardown.
        }

        try
        {
            if (!_process.HasExited)
            {
                if (!_process.WaitForExit((int)_options.ShutdownGracePeriod.TotalMilliseconds))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit();
                }
            }
        }
        catch
        {
            // Best-effort teardown - Dispose should never throw.
        }

        try
        {
            _heartbeatLoopTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best-effort - the CancellationTokenSource above already asked it to stop.
        }

        _heartbeatCts?.Dispose();
        _connectionLock.Dispose();
        _process.Dispose();
    }
}

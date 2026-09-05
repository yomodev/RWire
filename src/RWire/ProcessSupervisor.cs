using System.Buffers;
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
    Busy,
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
    private static long _nextSessionId;

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

    /// <summary>
    /// Identifies this process's session. RHandles are stamped with
    /// the SessionId of the supervisor that created them - using one
    /// after a (future Phase 6) restart, when this would change,
    /// throws rather than silently addressing a new process's
    /// registry with a stale id. Fixed for the lifetime of this
    /// instance today, since Phase 6's restart logic doesn't exist
    /// yet - the field exists now so handles are already
    /// restart-safe by construction once it does.
    /// </summary>
    public ulong SessionId { get; } = (ulong)Interlocked.Increment(ref _nextSessionId);

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
            if (State is SupervisorState.Starting or SupervisorState.Ready or SupervisorState.Busy)
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
        string receivedToken = WireStrings.Read(payload, ref offset);
        string rVersion = WireStrings.Read(payload, ref offset);

        if (receivedToken != _token)
        {
            throw new InvalidOperationException(
                "Handshake token mismatch - a stray or previous R process may have " +
                "connected to this listener.");
        }

        RVersion = rVersion;
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

    // -----------------------------------------------------------------
    // EVAL / CALL (docs/spec.md sections 4.4 and 5) - both sync and
    // async variants, sharing the same encode/decode logic, neither
    // derived from the other (same principle as RConnection).
    // -----------------------------------------------------------------

    private void EnsureReady()
    {
        if (State != SupervisorState.Ready)
        {
            throw new InvalidOperationException(
                $"Cannot make a call while in state {State} (must be Ready).");
        }
    }

    /// <summary>
    /// Evaluates an arbitrary R expression and returns its value.
    /// Throws RErrorException for a caught R-side error (connection
    /// stays healthy) or other exceptions for a protocol/connection
    /// failure (connection is marked Faulted).
    /// </summary>
    public RValue Eval(string expression)
    {
        EnsureReady();
        _connectionLock.Wait();
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            _connection.Send(MsgType.Eval, correlationId, EncodeEvalPayload(expression));
            using Frame response = _connection.Receive();
            RValue result = DecodeResponse(response);
            State = SupervisorState.Ready;
            return result;
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Async counterpart of <see cref="Eval"/>.</summary>
    public async Task<RValue> EvalAsync(string expression, CancellationToken ct = default)
    {
        EnsureReady();
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            await _connection.SendAsync(MsgType.Eval, correlationId, EncodeEvalPayload(expression), ct)
                .ConfigureAwait(false);
            using Frame response = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
            RValue result = DecodeResponse(response);
            State = SupervisorState.Ready;
            return result;
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Invokes a named R function with the given arguments (all
    /// inline values in Phase 2 - handle-typed arguments arrive in
    /// Phase 3) and returns its value.
    /// </summary>
    /// <summary>
    /// Invokes a named R function with the given arguments - each
    /// argument is either an inline RValue or an RHandle (implicitly
    /// convertible to RCallArgument), resolved on the R side without
    /// a handle's underlying data crossing the wire.
    /// </summary>
    public RValue Call(string functionName, IReadOnlyList<RCallArgument> arguments)
    {
        EnsureReady();
        ValidateHandleArguments(arguments);
        _connectionLock.Wait();
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            _connection.Send(MsgType.Call, correlationId, EncodeCallPayload(functionName, arguments));
            using Frame response = _connection.Receive();
            RValue result = DecodeResponse(response);
            State = SupervisorState.Ready;
            return result;
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Async counterpart of <see cref="Call"/>.</summary>
    public async Task<RValue> CallAsync(
        string functionName, IReadOnlyList<RCallArgument> arguments, CancellationToken ct = default)
    {
        EnsureReady();
        ValidateHandleArguments(arguments);
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            await _connection
                .SendAsync(MsgType.Call, correlationId, EncodeCallPayload(functionName, arguments), ct)
                .ConfigureAwait(false);
            using Frame response = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
            RValue result = DecodeResponse(response);
            State = SupervisorState.Ready;
            return result;
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void ValidateHandleArguments(IReadOnlyList<RCallArgument> arguments)
    {
        foreach (RCallArgument arg in arguments)
        {
            if (arg.IsHandle)
            {
                ValidateHandle(arg.Handle);
                _ = arg.Handle.Id; // throws ObjectDisposedException up front if already released,
                                   // before entering the connection lock / Busy state below - a
                                   // disposed-handle mistake is a client bug, not a connection
                                   // failure, and must not fault the supervisor.
            }
        }
    }

    /// <summary>
    /// Throws if handle belongs to a different (e.g. pre-restart)
    /// session than this supervisor - see RHandle's and SessionId's
    /// doc comments.
    /// </summary>
    private void ValidateHandle(RHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.SessionId != SessionId)
        {
            throw new ObjectDisposedException(
                nameof(RHandle),
                "This handle belongs to a previous R process session and is no longer valid.");
        }
    }

    // -----------------------------------------------------------------
    // Object registry: SET_OBJ / GET_OBJ / CREATE_REF / RELEASE_REF
    // (docs/spec.md section 8). Refcounting lives entirely on the R
    // side - RHandle here is a thin, disposable proxy.
    // -----------------------------------------------------------------

    /// <summary>Stores a value in the R worker's object registry and returns a handle to it (refcount starts at 1).</summary>
    public async Task<RHandle> SetObjAsync(RValue value, CancellationToken ct = default)
    {
        EnsureReady();
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            var writer = new ArrayBufferWriter<byte>();
            RValueCodec.Encode(writer, value);
            await _connection.SendAsync(MsgType.SetObj, correlationId, writer.WrittenMemory, ct)
                .ConfigureAwait(false);
            using Frame response = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
            long id = DecodeHandleIdResult(response);
            State = SupervisorState.Ready;
            return new RHandle(this, SessionId, id);
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Sync counterpart of <see cref="SetObjAsync"/>.</summary>
    public RHandle SetObj(RValue value)
    {
        EnsureReady();
        _connectionLock.Wait();
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            var writer = new ArrayBufferWriter<byte>();
            RValueCodec.Encode(writer, value);
            _connection.Send(MsgType.SetObj, correlationId, writer.WrittenSpan);
            using Frame response = _connection.Receive();
            long id = DecodeHandleIdResult(response);
            State = SupervisorState.Ready;
            return new RHandle(this, SessionId, id);
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Fetches the value referenced by handle.</summary>
    public async Task<RValue> GetObjAsync(RHandle handle, CancellationToken ct = default)
    {
        ValidateHandle(handle);
        long id = handle.Id; // throws ObjectDisposedException up front, before Busy/lock, if already released
        EnsureReady();
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            await _connection.SendAsync(MsgType.GetObj, correlationId, EncodeHandleId(id), ct)
                .ConfigureAwait(false);
            using Frame response = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
            RValue result = DecodeResponse(response);
            State = SupervisorState.Ready;
            return result;
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Sync counterpart of <see cref="GetObjAsync"/>.</summary>
    public RValue GetObj(RHandle handle)
    {
        ValidateHandle(handle);
        long id = handle.Id; // throws ObjectDisposedException up front, before Busy/lock, if already released
        EnsureReady();
        _connectionLock.Wait();
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            _connection.Send(MsgType.GetObj, correlationId, EncodeHandleId(id));
            using Frame response = _connection.Receive();
            RValue result = DecodeResponse(response);
            State = SupervisorState.Ready;
            return result;
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Increments the R-side refcount for handle's object and returns a new, independently-disposable handle to it.</summary>
    public async Task<RHandle> CreateRefAsync(RHandle handle, CancellationToken ct = default)
    {
        ValidateHandle(handle);
        long id = handle.Id; // throws ObjectDisposedException up front, before Busy/lock, if already released
        EnsureReady();
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            await _connection.SendAsync(MsgType.CreateRef, correlationId, EncodeHandleId(id), ct)
                .ConfigureAwait(false);
            using Frame response = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
            EnsureSuccessAck(response);
            State = SupervisorState.Ready;
            return new RHandle(this, SessionId, id);
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Sync counterpart of <see cref="CreateRefAsync"/>.</summary>
    public RHandle CreateRef(RHandle handle)
    {
        ValidateHandle(handle);
        long id = handle.Id; // throws ObjectDisposedException up front, before Busy/lock, if already released
        EnsureReady();
        _connectionLock.Wait();
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            _connection.Send(MsgType.CreateRef, correlationId, EncodeHandleId(id));
            using Frame response = _connection.Receive();
            EnsureSuccessAck(response);
            State = SupervisorState.Ready;
            return new RHandle(this, SessionId, id);
        }
        catch (RErrorException)
        {
            State = SupervisorState.Ready;
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Sends RELEASE_REF for id. Internal - the intended caller is
    /// RHandle.Dispose()/finalizer via ReleaseHandleBestEffort; exposed
    /// at this level (rather than only the fire-and-forget wrapper) so
    /// tests can await completion and assert on failures directly,
    /// which the best-effort path deliberately swallows.
    /// </summary>
    internal async Task ReleaseRefAsync(long id, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        bool wasReady = State == SupervisorState.Ready;
        if (wasReady)
        {
            State = SupervisorState.Busy;
        }

        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            await _connection.SendAsync(MsgType.ReleaseRef, correlationId, EncodeHandleId(id), ct)
                .ConfigureAwait(false);
            using Frame response = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
            EnsureSuccessAck(response);
            if (wasReady)
            {
                State = SupervisorState.Ready;
            }
        }
        catch (RErrorException)
        {
            if (wasReady)
            {
                State = SupervisorState.Ready;
            }
            throw;
        }
        catch
        {
            State = SupervisorState.Faulted;
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Called from RHandle.Dispose()/finalizer. Fire-and-forget and
    /// exception-swallowing by design: Dispose must never throw, a
    /// finalizer thread cannot safely be blocked on, and a handle
    /// whose session has already ended has nothing left to release.
    /// </summary>
    internal void ReleaseHandleBestEffort(ulong sessionId, long id)
    {
        if (sessionId != SessionId || State != SupervisorState.Ready)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ReleaseRefAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort - see summary above.
            }
        });
    }

    private static byte[] EncodeHandleId(long id)
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, id);
        return buffer;
    }

    private static long DecodeHandleIdResult(Frame response)
    {
        if (response.MsgType == MsgType.Error)
        {
            int offset = 0;
            throw new RErrorException(WireStrings.Read(response.Payload.Span, ref offset));
        }

        if (response.MsgType != MsgType.Result || response.Payload.Length != 8)
        {
            throw new InvalidOperationException(
                $"Expected an 8-byte handle id in RESULT, got {response.MsgType} " +
                $"with {response.Payload.Length} payload bytes.");
        }

        return BinaryPrimitives.ReadInt64LittleEndian(response.Payload.Span);
    }

    private static void EnsureSuccessAck(Frame response)
    {
        if (response.MsgType == MsgType.Error)
        {
            int offset = 0;
            throw new RErrorException(WireStrings.Read(response.Payload.Span, ref offset));
        }

        if (response.MsgType != MsgType.Result)
        {
            throw new InvalidOperationException($"Expected RESULT or ERROR, got {response.MsgType}.");
        }
    }

    private static byte[] EncodeEvalPayload(string expression)
    {
        var writer = new ArrayBufferWriter<byte>();
        WireStrings.Write(writer, expression);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeCallPayload(string functionName, IReadOnlyList<RCallArgument> arguments)
    {
        var writer = new ArrayBufferWriter<byte>();
        WireStrings.Write(writer, functionName);

        Span<byte> countSpan = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(countSpan, arguments.Count);
        writer.Advance(4);

        foreach (RCallArgument arg in arguments)
        {
            Span<byte> isHandleSpan = writer.GetSpan(1);
            isHandleSpan[0] = (byte)(arg.IsHandle ? 1 : 0);
            writer.Advance(1);

            if (arg.IsHandle)
            {
                long id = arg.Handle.Id; // throws ObjectDisposedException if released
                Span<byte> idSpan = writer.GetSpan(8);
                BinaryPrimitives.WriteInt64LittleEndian(idSpan, id);
                writer.Advance(8);
            }
            else
            {
                RValueCodec.Encode(writer, arg.Value);
            }
        }

        return writer.WrittenSpan.ToArray();
    }

    private static RValue DecodeResponse(Frame response)
    {
        if (response.MsgType == MsgType.Error)
        {
            int offset = 0;
            throw new RErrorException(WireStrings.Read(response.Payload.Span, ref offset));
        }

        if (response.MsgType != MsgType.Result)
        {
            throw new InvalidOperationException(
                $"Expected RESULT or ERROR, got {response.MsgType}.");
        }

        return RValueCodec.Decode(response.Payload.Span);
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

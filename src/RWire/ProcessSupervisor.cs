using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    /// valid HELLO frame before treating startup (or a restart
    /// attempt) as failed.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for the R process to exit on its own after a
    /// SHUTDOWN frame is sent during Dispose, before force-killing it.
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often to send a PING while the connection is idle (State == Ready).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a PONG before treating the connection as faulted.</summary>
    public TimeSpan HeartbeatResponseTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Maximum number of automatic restart attempts after a fault
    /// (docs/spec.md section 3.4) before giving up permanently. Each
    /// attempt is preceded by an exponential backoff delay - see
    /// InitialRestartDelay/MaxRestartDelay.
    /// </summary>
    public int MaxRestartAttempts { get; init; } = 5;

    /// <summary>Delay before the first restart attempt; doubles each subsequent attempt up to MaxRestartDelay.</summary>
    public TimeSpan InitialRestartDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Upper bound on the exponential backoff delay between restart attempts.</summary>
    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a call (Eval/Call/GetObj/...) will block waiting for
    /// an in-progress automatic restart to settle before giving up and
    /// throwing. This is what makes "the supervisor recovers
    /// automatically for the next call" (docs/spec.md section 11,
    /// Phase 6 exit criteria) transparent to ordinary callers instead
    /// of requiring them to catch a Restarting-state exception and
    /// retry manually.
    /// </summary>
    public TimeSpan RestartWaitTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How many recent stdout/stderr lines to retain for correlating with a fault (docs/spec.md section 11, Phase 6: "log correlation with the failure event").</summary>
    public int RecentDiagnosticLineCapacity { get; init; } = 20;
}

/// <summary>
/// Full lifecycle state machine (docs/spec.md section 3.2).
/// </summary>
public enum SupervisorState
{
    NotStarted,
    Starting,
    Ready,
    Busy,
    Faulted,
    Restarting,
    Disposed,
}

/// <summary>
/// Launches the R worker process, performs the real HELLO handshake
/// over the frame protocol, keeps the connection alive with a
/// PING/PONG heartbeat, automatically restarts the worker (with
/// exponential backoff) if it crashes or stops responding, and shuts
/// it down gracefully (or force-kills it) on Dispose.
///
/// Restart is transparent to callers: EnsureReady() (invoked by every
/// Eval/Call/GetObj/... method) blocks briefly for an in-progress
/// restart to settle rather than immediately rejecting the call, so a
/// caller that happens to call in right after a crash generally just
/// sees a short delay rather than an exception - see
/// RWireOptions.RestartWaitTimeout. A call that is itself in flight
/// when the crash happens still fails with a clear exception (docs/
/// spec.md section 11, Phase 6 exit criteria) rather than being
/// silently retried - only the *next* call benefits from the
/// already-recovered connection.
///
/// A successful restart gets a new SessionId, so RHandles created
/// before the restart are correctly rejected afterward (ValidateHandle
/// already checks this - see RHandle's doc comment) without any
/// restart-specific handle-invalidation logic being needed.
///
/// See docs/phases/phase-6-process-supervision.md for the checklist
/// this class implements against.
/// </summary>
public sealed class ProcessSupervisor : IDisposable
{
    private static long _nextSessionId;

    private readonly RWireOptions _options;
    private readonly Func<IRChannelListener> _channelListenerFactory;
    private readonly ILogger<ProcessSupervisor> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _recentDiagnosticsLock = new();
    private readonly Queue<string> _recentDiagnostics = new();

    private Process _process;
    private IRChannelListener _channelListener;
    private string _token;
    private RConnection? _connection;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatLoopTask;
    private Task? _stdoutPumpTask;
    private Task? _stderrPumpTask;
    private int _restartGate; // Interlocked guard: 0 = idle, 1 = a restart loop is running
    private bool _disposed;
    private readonly EventHandler _processExitHandler;

    public SupervisorState State { get; private set; } = SupervisorState.NotStarted;

    /// <summary>
    /// Identifies the current process session. RHandles are stamped
    /// with the SessionId active when they were created; using one
    /// after a restart (when this changes) throws rather than
    /// silently addressing the new process's registry with a stale id
    /// - see RHandle's and ValidateHandle's doc comments.
    /// </summary>
    public ulong SessionId { get; private set; }

    /// <summary>
    /// The ephemeral loopback port the current R process was told to
    /// connect back to, when the active listener is TCP-based. -1 for
    /// a non-TCP channel (e.g. a future named-pipe listener), where
    /// there is no port at all - use ChannelArgument for anything
    /// channel-agnostic. Changes across restarts.
    /// </summary>
    public int Port => int.TryParse(_channelListener.ChannelArgument, out int port) ? port : -1;

    /// <summary>
    /// Whatever value was passed to the R worker's --endpoint=
    /// argument to connect back to the current listener - a port
    /// number as a string for the default TCP listener, or something
    /// else (a pipe name, "host:port", ...) for a different
    /// IRChannelListener implementation. Changes across restarts.
    /// </summary>
    public string ChannelArgument => _channelListener.ChannelArgument;

    /// <summary>The R version string reported in the HELLO frame, populated after StartAsync (or a restart) completes.</summary>
    public string? RVersion { get; private set; }

    /// <summary>
    /// The process's exit code, populated once Dispose has confirmed
    /// the (current) process has actually exited. Null before that.
    /// Exists so callers/tests can check exit status without touching
    /// the underlying Process object after Dispose has released it -
    /// Process throws InvalidOperationException ("No process is
    /// associated with this object") if its properties are accessed
    /// post-Dispose, so this is captured proactively instead.
    /// </summary>
    public int? ExitCode { get; private set; }

    /// <summary>How many restart attempts have completed successfully over this instance's lifetime.</summary>
    public int RestartCount { get; private set; }

    /// <summary>True once MaxRestartAttempts has been exhausted without a successful restart - State stays Faulted permanently at that point; no further automatic restarts will be attempted.</summary>
    public bool IsPermanentlyFailed { get; private set; }

    /// <summary>The exception that most recently caused a fault (an unexpected process exit, a heartbeat timeout, or a failed call) - null if nothing has faulted yet.</summary>
    public Exception? LastFault { get; private set; }

    /// <summary>
    /// The most recent stdout/stderr lines (bounded by
    /// RWireOptions.RecentDiagnosticLineCapacity), for correlating
    /// with a fault after the fact (docs/spec.md section 11, Phase 6).
    /// </summary>
    public IReadOnlyList<string> RecentDiagnosticOutput
    {
        get
        {
            lock (_recentDiagnosticsLock)
            {
                return _recentDiagnostics.ToArray();
            }
        }
    }

    /// <summary>
    /// Raised for every line the R process writes to stdout or
    /// stderr. isError is true for stderr lines. Read via two
    /// dedicated async ReadLineAsync loops (not the
    /// BeginOutputReadLine/OutputDataReceived event pattern) so
    /// Dispose can deterministically await both streams draining
    /// fully rather than guessing with a fixed delay.
    /// </summary>
    public event Action<string, bool>? DiagnosticOutput;

    /// <summary>Exposed for test purposes only (see AssemblyInfo.cs's InternalsVisibleTo). Reflects whichever process is currently active - reassigned across restarts.</summary>
    internal Process ProcessForTesting => _process;

    /// <summary>Uses the default TCP loopback channel listener.</summary>
    public ProcessSupervisor(RWireOptions options, ILogger<ProcessSupervisor>? logger = null)
        : this(options, static () => new TcpRChannelListener(), logger)
    {
    }

    /// <summary>
    /// Uses channelListenerFactory to create a fresh IRChannelListener
    /// for the initial launch and for every subsequent restart - a
    /// factory (not a single instance) because a listener generally
    /// can't be reused once its connection has been accepted and
    /// disposed. This is the extension point for tests or for a
    /// future non-socket channel. ProcessSupervisor never constructs a
    /// TcpListener/TcpClient itself; it only depends on
    /// IRChannelListener.
    ///
    /// logger is optional - RWire never forces a logging backend on
    /// the host application. Pass an ILogger&lt;ProcessSupervisor&gt;
    /// from whatever ILoggerFactory your app already uses (Serilog,
    /// NLog via Microsoft.Extensions.Logging.NLog, the built-in
    /// console/file providers, ...); if omitted, logging is a no-op.
    /// </summary>
    public ProcessSupervisor(
        RWireOptions options, Func<IRChannelListener> channelListenerFactory, ILogger<ProcessSupervisor>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _channelListenerFactory = channelListenerFactory ?? throw new ArgumentNullException(nameof(channelListenerFactory));
        _logger = logger ?? NullLogger<ProcessSupervisor>.Instance;
        (_process, _channelListener, _token) = CreateProcessAndListener();

        // Best-effort orphan mitigation, not a guarantee - see
        // KillCurrentProcessBestEffort's doc comment for exactly what
        // this does and doesn't cover. Unsubscribed in Dispose().
        _processExitHandler = (_, _) => KillCurrentProcessBestEffort();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    private (Process Process, IRChannelListener Listener, string Token) CreateProcessAndListener()
    {
        string token = Guid.NewGuid().ToString("N");
        IRChannelListener listener = _channelListenerFactory();

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
        startInfo.ArgumentList.Add($"--endpoint={listener.ChannelArgument}");
        startInfo.ArgumentList.Add($"--token={token}");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnProcessExited();

        return (process, listener, token);
    }

    private void OnProcessExited()
    {
        if (State is SupervisorState.Starting or SupervisorState.Ready or SupervisorState.Busy)
        {
            _logger.LogWarning("R worker process exited unexpectedly while in state {State}", State);
            Fault(new InvalidOperationException(
                "The R worker process exited unexpectedly. Recent output:\n" +
                string.Join('\n', RecentDiagnosticOutput)));
        }
    }

    /// <summary>
    /// Registered against AppDomain.CurrentDomain.ProcessExit to
    /// reduce - not eliminate - the chance of an orphaned R process if
    /// the host application exits without ever calling Dispose().
    ///
    /// IMPORTANT - what this does and does not cover: ProcessExit
    /// fires for an ordinary managed exit (Main() returning normally,
    /// Environment.Exit(), an unhandled exception unwinding to the
    /// runtime's default handler, Ctrl+C via
    /// AppDomain/Console.CancelJoinableTasks-style shutdown on most
    /// platforms). It does NOT fire for a hard kill of the host
    /// process (kill -9 / SIGKILL, Task Manager "End Process" on
    /// Windows, a container being forcibly stopped, or a power loss) -
    /// those bypass all managed shutdown code on any platform, and
    /// nothing running inside the process being killed can react to
    /// them, full stop. A hard-killed host will still orphan its R
    /// child process with this mitigation in place.
    ///
    /// Properly closing that gap requires an OS-level mechanism
    /// external to this process - Windows Job Objects with
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, or Linux's
    /// prctl(PR_SET_PDEATHSIG) - both of which need P/Invoke and are
    /// deliberately out of scope here; see
    /// docs/phases/phase-8-plan.md for that as a concrete follow-up.
    /// </summary>
    private void KillCurrentProcessBestEffort()
    {
        try
        {
            if (!_disposed && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort - see this method's summary above for what
            // it can't cover; failing here just means the same gap.
        }
    }

    /// <summary>
    /// Reads lines from reader and raises DiagnosticOutput for each,
    /// until EOF (the process closed the stream, normally because it
    /// exited). Also feeds RecentDiagnosticOutput's bounded history.
    /// Runs as a background task started right after Process.Start()
    /// so early output - including from a process that fails almost
    /// immediately - is captured well before any caller gets around to
    /// checking DiagnosticOutput.
    /// </summary>
    private async Task PumpStreamAsync(TextReader reader, bool isError)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            lock (_recentDiagnosticsLock)
            {
                _recentDiagnostics.Enqueue(line);
                while (_recentDiagnostics.Count > _options.RecentDiagnosticLineCapacity)
                {
                    _recentDiagnostics.Dequeue();
                }
            }

            DiagnosticOutput?.Invoke(line, isError);
        }
    }

    /// <summary>
    /// Starts the R process, waits for it to connect back, validates
    /// the HELLO handshake, and starts the heartbeat loop. Throws if
    /// the process fails to launch, never connects/sends HELLO within
    /// HandshakeTimeout, or the handshake token doesn't match. Unlike
    /// a later automatic restart, a failure here is never retried -
    /// if Rscript itself can't be found, or the worker script path is
    /// wrong, retrying with the same (broken) configuration wouldn't
    /// help; the caller finds out immediately instead of waiting
    /// through MaxRestartAttempts' worth of backoff first.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State != SupervisorState.NotStarted)
        {
            throw new InvalidOperationException(
                $"StartAsync can only be called once, from NotStarted (current state: {State}).");
        }

        State = SupervisorState.Starting;
        _logger.LogInformation(
            "Starting R worker via '{RScriptPath} {WorkerScriptPath}' on endpoint {ChannelArgument}",
            _options.RScriptPath, _options.WorkerScriptPath, _channelListener.ChannelArgument);

        try
        {
            await LaunchAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "R worker started successfully (R version: {RVersion}, session {SessionId})",
                RVersion, SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the R worker");
            State = SupervisorState.Faulted;
            throw;
        }
    }

    /// <summary>
    /// The shared core of "start the currently-assigned _process,
    /// accept its connect-back, complete the HELLO handshake, start
    /// the heartbeat loop" - used by both the initial StartAsync and
    /// every automatic restart attempt. Does not itself set State on
    /// failure or catch exceptions; StartAsync and RestartLoopAsync
    /// each handle that differently (throw immediately vs. retry).
    /// </summary>
    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        bool started;
        try
        {
            started = _process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start '{_options.RScriptPath}'. Is Rscript on PATH?", ex);
        }

        if (!started)
        {
            throw new InvalidOperationException("Process.Start returned false.");
        }

        _stdoutPumpTask = PumpStreamAsync(_process.StandardOutput, isError: false);
        _stderrPumpTask = PumpStreamAsync(_process.StandardError, isError: true);

        using var timeoutCts = new CancellationTokenSource(_options.HandshakeTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        IRChannel channel;
        try
        {
            channel = await _channelListener.AcceptAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {_options.HandshakeTimeout} waiting for the R worker to connect back. " +
                $"Recent output:\n{string.Join('\n', RecentDiagnosticOutput)}");
        }

        _connection = new RConnection(channel);

        try
        {
            await ReceiveAndValidateHelloAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {_options.HandshakeTimeout} waiting for the HELLO frame.");
        }

        SessionId = (ulong)Interlocked.Increment(ref _nextSessionId);
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

    // -----------------------------------------------------------------
    // Automatic restart (docs/spec.md sections 3.4, 11 Phase 6)
    // -----------------------------------------------------------------

    /// <summary>
    /// Records cause as the current fault and, unless a restart is
    /// already underway or the supervisor is disposed, transitions to
    /// Restarting and kicks off the background restart loop. Safe to
    /// call from multiple places observing the same underlying failure
    /// (the heartbeat loop, Process.Exited, and an in-flight call's
    /// own catch block can all race to report the same crash) - only
    /// one restart loop ever runs at a time.
    /// </summary>
    private void Fault(Exception cause)
    {
        LastFault = cause;

        if (_disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _restartGate, 1, 0) != 0)
        {
            // A restart is already in progress from another trigger -
            // just make sure State reflects it.
            State = SupervisorState.Restarting;
            return;
        }

        _logger.LogWarning(cause, "Supervisor faulted - starting automatic restart");
        State = SupervisorState.Restarting;
        _ = Task.Run(RestartLoopAsync);
    }

    private async Task RestartLoopAsync()
    {
        try
        {
            for (int attempt = 1; attempt <= _options.MaxRestartAttempts && !_disposed; attempt++)
            {
                TimeSpan delay = ComputeBackoffDelay(attempt);
                _logger.LogInformation(
                    "Restart attempt {Attempt}/{MaxAttempts} in {DelayMs} ms",
                    attempt, _options.MaxRestartAttempts, delay.TotalMilliseconds);
                try
                {
                    await Task.Delay(delay, _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // disposed mid-backoff
                }

                try
                {
                    CleanupForRestart();
                    (_process, _channelListener, _token) = CreateProcessAndListener();
                    await LaunchAsync(_lifetimeCts.Token).ConfigureAwait(false);
                    RestartCount++;
                    _logger.LogInformation(
                        "Restart attempt {Attempt} succeeded - new session {SessionId} on endpoint {ChannelArgument}",
                        attempt, SessionId, _channelListener.ChannelArgument);
                    return; // LaunchAsync already set State = Ready
                }
                catch (Exception ex)
                {
                    LastFault = ex;
                    _logger.LogWarning(ex, "Restart attempt {Attempt} failed", attempt);
                }
            }

            if (!_disposed)
            {
                IsPermanentlyFailed = true;
                State = SupervisorState.Faulted;
                _logger.LogError(
                    "Giving up after {MaxAttempts} restart attempts - supervisor is permanently failed",
                    _options.MaxRestartAttempts);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _restartGate, 0);
        }
    }

    private TimeSpan ComputeBackoffDelay(int attempt)
    {
        double multiplier = Math.Pow(2, attempt - 1);
        double delayMs = _options.InitialRestartDelay.TotalMilliseconds * multiplier;
        double cappedMs = Math.Min(delayMs, _options.MaxRestartDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(cappedMs);
    }

    /// <summary>Tears down everything associated with the current (failed) process/connection before a restart attempt builds fresh ones. Every step is best-effort - the objects being cleaned up are already known-broken.</summary>
    private void CleanupForRestart()
    {
        try { _heartbeatCts?.Cancel(); } catch { /* best-effort */ }
        try { _connection?.Dispose(); } catch { /* best-effort */ }
        try { _channelListener.Dispose(); } catch { /* best-effort */ }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* best-effort */ }
        try { _process.Dispose(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Blocks (bounded by RWireOptions.RestartWaitTimeout) while
    /// State == Restarting, so an ordinary call made shortly after a
    /// crash generally just waits briefly rather than throwing - see
    /// this class's own doc comment for why this is a synchronous
    /// poll rather than a proper async wait (used from both the sync
    /// and async call surfaces).
    /// </summary>
    private void WaitForRestartToSettle()
    {
        DateTime deadline = DateTime.UtcNow + _options.RestartWaitTimeout;
        while (State == SupervisorState.Restarting && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }
    }

    // -----------------------------------------------------------------
    // EVAL / CALL (docs/spec.md sections 4.4 and 5) - both sync and
    // async variants, sharing the same encode/decode logic, neither
    // derived from the other (same principle as RConnection).
    // -----------------------------------------------------------------

    /// <summary>
    /// Rejects genuinely unusable states before a caller queues on
    /// _connectionLock. Waits out an in-progress restart first (see
    /// WaitForRestartToSettle) rather than treating Restarting as an
    /// immediate rejection. Deliberately does NOT reject Busy: Busy
    /// means "another call is currently using the connection", which
    /// is a normal, transient condition for any concurrent caller to
    /// observe - the connection lock below is what serializes actual
    /// access, so a concurrent caller should queue and wait its turn,
    /// not be thrown at just because it happened to check State while
    /// someone else's request was in flight.
    /// </summary>
    private void EnsureReady()
    {
        if (State == SupervisorState.Restarting)
        {
            WaitForRestartToSettle();
        }

        switch (State)
        {
            case SupervisorState.Ready:
            case SupervisorState.Busy:
                return;
            case SupervisorState.Faulted:
                throw new InvalidOperationException(
                    IsPermanentlyFailed
                        ? $"Cannot make a call: the connection failed permanently after " +
                          $"{_options.MaxRestartAttempts} restart attempts. Last fault: {LastFault?.Message}"
                        : $"Cannot make a call: the connection is Faulted. Last fault: {LastFault?.Message}");
            case SupervisorState.Disposed:
                throw new ObjectDisposedException(nameof(ProcessSupervisor));
            case SupervisorState.Restarting:
                throw new TimeoutException(
                    $"Timed out after {_options.RestartWaitTimeout} waiting for an in-progress restart to settle.");
            default:
                throw new InvalidOperationException(
                    $"Cannot make a call before StartAsync has completed (current state: {State}).");
        }
    }

    /// <summary>
    /// Evaluates an arbitrary R expression and returns its value.
    /// Throws RErrorException for a caught R-side error (connection
    /// stays healthy) or other exceptions for a protocol/connection
    /// failure (triggers an automatic restart - see Fault).
    /// </summary>
    public RValue Eval(string expression)
    {
        EnsureReady();
        _connectionLock.Wait();
        State = SupervisorState.Busy;
        try
        {
            uint correlationId = _connection!.NextCorrelationId();
            _connection.Send(MsgType.Eval, correlationId, EncodeEvalPayload(expression).WrittenSpan);
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
        catch (Exception ex)
        {
            Fault(ex);
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
            await _connection.SendAsync(MsgType.Eval, correlationId, EncodeEvalPayload(expression).WrittenMemory, ct)
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
        catch (Exception ex)
        {
            Fault(ex);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

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
            _connection.Send(MsgType.Call, correlationId, EncodeCallPayload(functionName, arguments).WrittenSpan);
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
        catch (Exception ex)
        {
            Fault(ex);
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
                .SendAsync(MsgType.Call, correlationId, EncodeCallPayload(functionName, arguments).WrittenMemory, ct)
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
        catch (Exception ex)
        {
            Fault(ex);
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
        catch (Exception ex)
        {
            Fault(ex);
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
        catch (Exception ex)
        {
            Fault(ex);
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
        catch (Exception ex)
        {
            Fault(ex);
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
        catch (Exception ex)
        {
            Fault(ex);
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
        catch (Exception ex)
        {
            Fault(ex);
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
        catch (Exception ex)
        {
            Fault(ex);
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
        catch (Exception ex)
        {
            Fault(ex);
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
            throw DecodeError(response.Payload.Span);
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
            throw DecodeError(response.Payload.Span);
        }

        if (response.MsgType != MsgType.Result)
        {
            throw new InvalidOperationException($"Expected RESULT or ERROR, got {response.MsgType}.");
        }
    }

    private static ArrayBufferWriter<byte> EncodeEvalPayload(string expression)
    {
        var writer = new ArrayBufferWriter<byte>();
        WireStrings.Write(writer, expression);
        return writer;
    }

    private static ArrayBufferWriter<byte> EncodeCallPayload(string functionName, IReadOnlyList<RCallArgument> arguments)
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

        return writer;
    }

    private static RValue DecodeResponse(Frame response)
    {
        if (response.MsgType == MsgType.Error)
        {
            throw DecodeError(response.Payload.Span);
        }

        if (response.MsgType != MsgType.Result)
        {
            throw new InvalidOperationException(
                $"Expected RESULT or ERROR, got {response.MsgType}.");
        }

        return RValueCodec.Decode(response.Payload.Span);
    }

    /// <summary>
    /// Decodes an ERROR frame's payload into an RErrorException.
    /// Wire shape (docs/spec.md section 4.2, ERROR):
    ///   [Message: Len(4)+UTF8] [ClassCount(4)] {Len(4)+UTF8}*ClassCount [HasCall(1)] [Call: Len(4)+UTF8]?
    /// This is a structured object sent over the protocol itself, not
    /// something inferred from stdout/stderr - the connection's data
    /// channel and the diagnostic-logging channel are deliberately
    /// separate (docs/spec.md section 2), and relying on stdout/stderr
    /// to communicate a specific request's failure back to the caller
    /// that made it would be fragile (ordering isn't guaranteed to
    /// line up with which request caused it, and there is nothing
    /// stopping arbitrary R code from writing to stdout/stderr itself).
    /// </summary>
    private static RErrorException DecodeError(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        string message = WireStrings.Read(payload, ref offset);

        int classCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
        offset += 4;
        var classes = new string[classCount];
        for (int i = 0; i < classCount; i++)
        {
            classes[i] = WireStrings.Read(payload, ref offset);
        }

        byte hasCall = payload[offset];
        offset += 1;
        string? call = hasCall == 1 ? WireStrings.Read(payload, ref offset) : null;

        return new RErrorException(message, classes, call);
    }

    /// <summary>
    /// Sends a graceful SHUTDOWN, closes the connection, and waits up
    /// to ShutdownGracePeriod for the process to exit before
    /// force-killing it. Also stops any in-progress or future
    /// automatic restart. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        State = SupervisorState.Disposed;
        _logger.LogInformation("Disposing - sending graceful shutdown to the R worker");

        try
        {
            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        }
        catch
        {
            // Best-effort teardown.
        }

        _lifetimeCts.Cancel(); // unblocks a RestartLoopAsync sitting in its backoff delay
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
            _channelListener.Dispose();
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

            // Captured now, before _process.Dispose() below - Process
            // throws "No process is associated with this object" if
            // its properties are read after Dispose, so anything a
            // caller might want to inspect post-Dispose (here, just
            // the exit code) must be pulled out proactively.
            ExitCode = _process.ExitCode;
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

        // The process has exited by this point (or was force-killed
        // above), so stdout/stderr are closed and these pump tasks
        // should complete almost immediately - awaiting them here
        // means DiagnosticOutput has definitely seen every line by the
        // time Dispose returns, rather than a caller having to guess
        // with an arbitrary delay.
        try
        {
            _stdoutPumpTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            _stderrPumpTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best-effort.
        }

        _heartbeatCts?.Dispose();
        _lifetimeCts.Dispose();
        _connectionLock.Dispose();
        _process.Dispose();
    }

    /// <summary>
    /// Sends a PING and awaits a PONG on the configured interval while
    /// State == Ready. Skips a tick (rather than blocking) if the
    /// connection is already in use by an application call, since a
    /// call in flight is itself evidence the connection is alive - see
    /// docs/spec.md section 3.3 and the non-goal of concurrent
    /// pipelining in section 1.2. A timeout or error here reports the
    /// fault through the same Fault() path everything else uses,
    /// triggering an automatic restart.
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
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Heartbeat-specific timeout (responseTimeoutCts), not
                // supervisor shutdown - the connection is unresponsive.
                Fault(ex);
            }
            catch (OperationCanceledException)
            {
                // Supervisor is disposing - exit the loop quietly.
                return;
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
            finally
            {
                _connectionLock.Release();
            }
        }
    }
}

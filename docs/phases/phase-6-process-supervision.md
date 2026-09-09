# Phase 6 — Process Supervision & Resilience

Status: implemented, unverified against a real build/test run of
*this specific phase* — Phases 1–5 have been confirmed working by the
user via manual build/test; this phase has not yet gone through that.

## Goal

Turn a crash or hang into a transient, automatically-recovered
condition instead of a permanent failure: detect it (process exit,
heartbeat timeout, or an in-flight call failing), restart the R worker
with exponential backoff, and make the recovery transparent to the
*next* caller — without silently retrying whichever call was actually
in flight when the crash happened.

## Reference

`../spec.md` §3.4 (restart policy), §11 (Phase 6 exit criteria),
§12.5 (chaos/resilience tests).

## What's implemented

- **Full `SupervisorState` state machine**: added `Restarting`
  alongside the existing `NotStarted`/`Starting`/`Ready`/`Busy`/
  `Faulted`/`Disposed`.
- **Connection creation is now restart-capable, not just DI'd once.**
  The constructor's `IRChannelListener` parameter became
  `Func<IRChannelListener> channelListenerFactory` (a **breaking
  change** from the earlier hardening pass — a listener generally
  can't be reused once its one connection has been accepted and
  disposed, so restart needs a way to mint a fresh one each attempt).
  The single-argument constructor (`new ProcessSupervisor(options)`)
  is unaffected — it defaults to `() => new TcpRChannelListener()` —
  so this didn't touch any existing call site.
- **`Fault(Exception)`** is the single place that reacts to a
  detected failure: records `LastFault`, and — unless already
  restarting or disposed — flips `State` to `Restarting` and kicks off
  `RestartLoopAsync` in the background. Called from three independent
  places that can all observe the same underlying crash: the
  heartbeat loop (timeout or protocol error), `Process.Exited`, and
  every `Eval`/`Call`/`GetObj`/... method's catch block for anything
  that isn't an `RErrorException` (a caught R-side condition, which
  stays non-fatal per Phase 2/3's existing design — this class
  deliberately keeps `RErrorException` handling and connection-failure
  handling as two clearly separate catch clauses everywhere). An
  `Interlocked` guard (`_restartGate`) ensures only one restart loop
  ever runs even if multiple triggers fire near-simultaneously.
- **Exponential backoff** (`ComputeBackoffDelay`): `InitialRestartDelay
  * 2^(attempt-1)`, capped at `MaxRestartDelay`, matching spec §3.4's
  200/400/800ms-capped example, both now configurable via
  `RWireOptions`.
- **`MaxRestartAttempts`**: after exhausting them, `IsPermanentlyFailed`
  becomes `true` and `State` stays `Faulted` — no further automatic
  attempts. `EnsureReady()` reports this distinctly from a merely
  transient fault in its exception message.
- **Transparent recovery for the next call**: `EnsureReady()` — called
  by every public method before it does anything else — blocks
  (bounded by `RestartWaitTimeout`, a synchronous poll) while
  `State == Restarting`, so an ordinary call made shortly after a
  crash typically just waits briefly rather than throwing. This is a
  deliberate sync-block-in-EnsureReady tradeoff rather than a proper
  async wait threaded through every call site — documented in
  `ProcessSupervisor`'s own class doc comment as acceptable because
  restarts are rare, not a hot path.
- **Handle invalidation falls out for free**: a successful restart
  mints a new `SessionId` (same field Phase 3 already used to reject
  handles from a different `ProcessSupervisor` instance) — no
  restart-specific handle-invalidation logic was needed at all, since
  `ValidateHandle`'s existing `handle.SessionId != SessionId` check
  already covers "this handle predates the current process."
- **Log correlation with the failure event** (spec §11's exact
  phrasing): a bounded ring buffer (`RecentDiagnosticOutput`,
  capacity `RWireOptions.RecentDiagnosticLineCapacity`) of the most
  recent stdout/stderr lines, fed by the same `PumpStreamAsync` tasks
  from the earlier hardening pass. Included directly in the
  `TimeoutException`/`InvalidOperationException` messages thrown by
  `LaunchAsync`/`OnProcessExited`, so a fault's exception message
  carries the R-side context that led to it.
- **Fatal-signature scanning was *not* implemented as an independent
  trigger.** Spec §3.3 says stdout/stderr scanning must be "a
  secondary signal, never the sole trigger" — rather than add a
  pattern-matcher whose only job would be to feed into a decision
  already made by more reliable signals (process exit, heartbeat
  timeout), the *log correlation* above achieves the actual goal
  (associating diagnostic output with a fault) without the false-
  positive risk of regex-matching R's freeform error text. See
  "Decisions changed since spec.md" in `../progress.md`.
- **`Dispose()` also stops any in-progress or future restart.** A new
  `_lifetimeCts`, cancelled first thing in `Dispose()`, unblocks a
  `RestartLoopAsync` sitting in its backoff `Task.Delay` so disposal
  doesn't have to wait out a pending retry.

## What this does NOT do

- No cap on *frequency* of crashes over time (e.g. "if this has
  restarted 10 times in the last minute, stop trying sooner") beyond
  the flat `MaxRestartAttempts` count — a fast-crash-loop and a
  slow-crash-loop consume the same attempt budget. Spec §12.5's
  "repeated rapid restart ... eventually surfaces a permanent-failure
  error rather than looping forever" is satisfied by
  `MaxRestartAttempts` alone; nothing more sophisticated (a rolling
  time window, jitter) was added.
- No attempt to preserve or replay any state across a restart beyond
  what naturally falls out of a fresh `SessionId` — outstanding
  handles are rejected (correctly), not migrated; a call in flight
  during the crash fails (correctly, per exit criteria) rather than
  being transparently retried against the new process.

## Checklist

- [ ] `dotnet build` succeeds with the constructor signature change
      (check `RTestthatSuiteTests`/anything else that might reference
      `IRChannelListener` directly — grepped clean during
      implementation, but worth a real compiler's opinion).
- [ ] `ExternalProcessKill_TriggersAutomaticRestart_AndSupervisorRecovers`
      passes — this is the core exit-criteria test: external kill →
      detected → automatically restarted → new `SessionId` → genuinely
      usable again (a real `EvalAsync` call succeeds after).
- [ ] `AfterAutomaticRestart_OldHandlesAreRejected_ViaSessionIdMismatch`
      passes.
- [ ] `CallMadeDuringRestart_WaitsForRecovery_ThenSucceeds` passes —
      this one has real timing sensitivity (a 100ms delay after the
      kill, hoping to land inside the restart window); if it's flaky,
      that's a test-tuning issue worth adjusting before assuming a
      real bug.
- [ ] `RestartExhaustion_AfterMaxAttempts_BecomesPermanentlyFailed`
      passes — uses a test-only `AlwaysFailingChannelListener` to
      simulate a worker that can never reconnect, without needing a
      genuinely broken R installation.

## Notes for resuming mid-phase

If restart tests are flaky: the three timing-bounded tests
(`CallMadeDuringRestart_...`, and the polling loops in the other two)
all use fairly generous deadlines (5–15s) against fast backoff
settings (100–500ms) specifically so they shouldn't be timing-critical
on a reasonably-provisioned machine — if they're still flaky, suspect
the machine/environment before the restart logic itself. If
`RestartExhaustion_...` is flaky specifically, check whether
`AlwaysFailingChannelListener.Port => 0` is causing the *real* spawned
R process (which does launch on every attempt, even though the C#
side sabotages the accept) to hang rather than fail fast when given
port 0 — that would be a test-fixture issue, not a restart-logic bug.

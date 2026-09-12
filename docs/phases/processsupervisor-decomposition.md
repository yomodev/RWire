# ProcessSupervisor Responsibility Decomposition

The user asked directly whether `ProcessSupervisor` is doing too much.
It is — it currently owns process lifecycle, channel/listener
management, the frame-level HELLO handshake, heartbeat, restart/
backoff, handle registry coordination (via `RHandle`), diagnostic
output capture, and logging, all in one 1,400+ line file (before this
pass; ~1,260 after). This document is the analysis: what was safe to
pull out mechanically today, and why the deeper split has to wait for
a real build/test environment rather than being attempted here.

## What was extracted this pass (low risk, done)

Two pieces were genuinely separable with no change in behavior and no
risk to the state-machine invariants below, because neither touches
`_process`, `_channelListener`, `_connection`, `State`, or
`_connectionLock`:

- **`ProcessSupervisorWireCodec`** (`src/RWire/ProcessSupervisorWireCodec.cs`)
  — the seven `EncodeEvalPayload`/`EncodeCallPayload`/`DecodeResponse`/
  `DecodeError`/`EncodeHandleId`/`DecodeHandleIdResult`/
  `EnsureSuccessAck` methods. All seven were already `private static`
  with zero instance dependency — pure functions over `Frame`/
  `ReadOnlySpan<byte>`/`ArrayBufferWriter<byte>`. Moving them out was a
  mechanical cut-paste-rename (`private static` → `internal static`,
  call sites prefixed with the new class name). No behavior change;
  every call site was updated, verified by grepping for any bare
  (unprefixed) reference left behind — none found.
- **`DiagnosticsBuffer`** (`src/RWire/DiagnosticsBuffer.cs`) — the
  bounded stdout/stderr ring buffer (`_recentDiagnostics`/
  `_recentDiagnosticsLock`) and the `PumpStreamAsync` method that fed
  it. This was self-contained: its own lock, its own state, and its
  only external input is a `TextReader` handed to it (the process's
  `StandardOutput`/`StandardError`) — it never reaches back into
  `ProcessSupervisor`'s other fields. `ProcessSupervisor` now holds a
  `DiagnosticsBuffer` instance, forwards its `LineRecorded` event to
  the existing public `DiagnosticOutput` event, and exposes
  `RecentDiagnosticOutput` as `_diagnostics.Recent`. Public API
  (`DiagnosticOutput` event, `RecentDiagnosticOutput` property) is
  byte-for-byte unchanged, which matters because
  `DiagnosticOutput_CapturesOutput_FromWorkerScript` in
  `ProcessSupervisorTests.cs` asserts against it directly.

Both extractions reduce `ProcessSupervisor.cs` by roughly 160 lines
combined and are, by design, invisible to every existing test's
assertions — nothing about *what* the class does changed, only where
two self-contained pieces of *how* live.

**Not yet built or run** (same caveat as every other Phase 7/8 change
in this sandbox) — this is mechanical enough that the risk is low, but
"low risk" isn't "verified." Add to the Phase 7 verification pass
(`docs/phases/phase-8-plan.md`).

## Why the deeper split (lifecycle / dispatch / supervision) waits

The natural-looking split is three pieces:

1. **Process/channel lifecycle** — start, restart, dispose:
   `CreateProcessAndListener`, `LaunchAsync`,
   `ReceiveAndValidateHelloAsync`, `CleanupForRestart`,
   `RestartLoopAsync`, `ComputeBackoffDelay`.
2. **Call dispatch** — `Eval`/`EvalAsync`/`Call`/`CallAsync`/
   `SetObj(Async)`/`GetObj(Async)`/`CreateRef(Async)`/`ReleaseRefAsync`,
   `EnsureReady`, `WaitForRestartToSettle`, handle validation.
3. **Supervision** — `HeartbeatLoopAsync`, `Fault`, `OnProcessExited`.

This is a reasonable *hypothesis*, and it's the one to evaluate first
whenever a real build/test environment is available. But tracing the
actual field usage shows why extracting it now, blind, would be
substantially riskier than the two pieces above — the risk isn't
"more lines," it's a specific, easy-to-miss synchronization subtlety:

- **`_connectionLock` only guards `_connection` for ordinary calls —
  not during restart.** Every Eval/Call/GetObj/etc. method acquires
  `_connectionLock` before touching `_connection`, and so does
  `HeartbeatLoopAsync`. But `RestartLoopAsync` → `CleanupForRestart`
  disposes `_connection` and reassigns `_process`/`_channelListener`/
  `_token` **without acquiring `_connectionLock` at all.** This isn't
  a bug — it's correct precisely *because* `State` is `Restarting`
  during that window, and `EnsureReady()` (called by every call method
  before it ever reaches the lock) blocks new callers on
  `WaitForRestartToSettle()` until `State` leaves `Restarting`. In
  other words: **the state machine (`State` plus `_restartGate`), not
  the semaphore, is what actually keeps restart-in-progress mutation
  of `_process`/`_channelListener`/`_connection` safe from a
  concurrent caller.** The semaphore only serializes *already-admitted*
  callers against each other and against the heartbeat.
- That means any decomposition has to either (a) keep `State`,
  `_restartGate`, `_connection`, `_process`, and `_channelListener`
  all owned by one component (which mostly just renames the coupling
  rather than removing it), or (b) genuinely redesign the
  synchronization story so a "lifecycle" component and a "dispatch"
  component can each reason about their own locking independently —
  which is a real design change to a correctness-critical path, not a
  mechanical extraction.
- `SessionId` is the other cross-cutting piece: it's bumped by the
  lifecycle code (`LaunchAsync`) but read by the dispatch code
  (`RHandle` validation, via `ProcessSupervisor.SessionId`) to detect
  a stale handle after a restart. Splitting lifecycle from dispatch
  means this either becomes a shared mutable field passed by
  reference between the two new components (again, coupling by
  another name) or an event/callback the dispatch component
  subscribes to — a real design decision, not a given.

None of this means the split is a bad idea — it may well be the right
end state. It means **attempting it in this sandbox, with no compiler
or test runner to catch a mistake in exactly this kind of
synchronization subtlety, is the wrong time to attempt it.** The two
extractions done this pass were chosen because they don't touch this
machinery at all; this one does, centrally.

## Recommendation

- Don't attempt the lifecycle/dispatch/supervision split blind. Wait
  for a session with a real .NET environment, do the Phase 7
  verification pass first (per `phase-8-plan.md`'s ordering), then
  attempt this with a build-and-test loop available to catch a
  synchronization mistake immediately rather than after the fact.
- When that session happens, the design question to answer first
  isn't "how do we split the methods" — it's "how does the next
  caller get safely blocked out during a restart if `State` /
  `_restartGate` don't all live in the same object as `_connection`."
  Answer that, then the method split follows naturally.
- In the meantime, `ProcessSupervisorWireCodec` and `DiagnosticsBuffer`
  stand on their own as real, if modest, reductions in what
  `ProcessSupervisor` has to hold in one place — and they're
  independently unit-testable now (pure functions; a buffer with an
  injected `TextReader`) without any of the process/connection
  machinery, which the monolithic version didn't allow.

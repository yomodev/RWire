# RWire — Progress Tracker

**How to resume this project in a new session (human or AI):**
1. Read this file first — it says which phase is current and what's
   actually done vs. still open.
2. Open `docs/spec.md` for the full locked-in design (architecture,
   protocol, type mapping, table format, rationale for rejected
   alternatives like Arrow), and `docs/spec-deviations.md` for every
   place implementation has diverged from it, organized by spec
   section.
3. Open `docs/phases/phase-N-*.md` for the current phase — each one is
   self-contained: goal, prerequisites, a concrete checklist, files to
   touch, and exit criteria, written so it doesn't require re-reading
   the whole conversation history that produced it.
4. Update the checklist and the "Current phase" line below as you go —
   this file is the source of truth for where things stand, not chat
   history. (This file was consolidated once Phases 1–5 were confirmed
   working by manual build/test, trimming session-by-session narrative
   in favor of current status — the per-phase docs still carry full
   implementation detail if you need the "why" behind something.)

Do not re-derive design decisions already settled in `spec.md` —
if something there seems wrong once implementation starts, record the
change in `docs/spec-deviations.md` rather than silently diverging.

---

## Current phase

**Phase 8 — feedback-driven hardening** (in progress). The user got
Phases 1–7 building and running for the first time and reported back:
one flaky test, some slow tests, several real compile errors/warnings
from a stricter analyzer setup than this sandbox has ever had
available, and a long list of substantive design questions and asks.
This phase works through that list. **Not everything below is done
yet** - see "Done this session" vs. "Still pending" below, and the
detailed forward plan in `docs/phases/phase-8-plan.md` (now written -
covers ordering/rationale for everything still pending).

### Done this session

- **Fixed the real compile error**: `TcpListener.AcceptTcpClientAsync(CancellationToken)`
  returns `ValueTask<TcpClient>`, not `Task<TcpClient>` - the earlier
  xUnit1051 fix introduced this by adding the token argument, which
  selects a different overload. Fixed in `RConnectionTests.cs`.
- **Fixed the flagged analyzer warnings** (CA1816 missing
  `GC.SuppressFinalize`, one instance each of IDE0300/CA1861 collection-
  literal/constant-array simplification at the specific lines cited).
  Not an exhaustive project-wide sweep - see "Still pending."
- **Fixed `DiagnosticOutput_CapturesOutput_OnWorkerScriptError`** based
  on real evidence: the user confirmed `Rscript` with a missing script
  path prints *nothing at all* on Windows, contradicting the test's
  entire premise. Replaced with a test using a real temporary R script
  that explicitly writes a deterministic line - testable on every
  platform, unlike relying on how (or whether) a missing-file error
  happens to be reported.
- **Generalized `IRChannelListener`** from an `int Port` to a generic
  `string ChannelArgument` - a named pipe or remote-TCP implementation
  doesn't have to invent a fake port number to satisfy the interface
  anymore. `TcpRChannelListener` implements it as the port number
  stringified; `ProcessSupervisor.Port` becomes a computed convenience
  (parses `ChannelArgument` as an int, -1 if it doesn't parse) so
  existing TCP-based code/tests didn't need to change. The R worker's
  CLI argument was renamed `--port=` → `--endpoint=` to match
  (`worker.R` updated correspondingly).
- **Socket performance tuning**: `TcpRChannelListener.AcceptAsync` now
  sets `NoDelay = true` (disables Nagle's algorithm - matters far more
  for RWire's small-frequent-request/response shape than for typical
  bulk transfer) and 1MB send/receive buffers.
- **`RConnection.Send`/`SendAsync` copy elimination carried over from
  Phase 7** - not new this session, but directly relevant to the
  "performance is quite shitty" feedback.
- **Logging**: `Microsoft.Extensions.Logging.Abstractions` added to
  `RWire.csproj`; `ProcessSupervisor` takes an optional
  `ILogger<ProcessSupervisor>` (both constructors, defaults to
  `NullLogger` - fully backward compatible, no existing call site
  needed to change) and logs start/success/failure, every restart
  attempt and its outcome, exhaustion, and Dispose. The test project
  wires in **NLog** (`TestLogging.cs`, console + per-run log file) and
  every `ProcessSupervisor` construction in `ProcessSupervisorTests.cs`
  and the shared fixture now passes it through - directly useful for
  investigating the flaky/slow tests.
- **Zombie-process mitigation (best-effort, honestly scoped)**:
  `ProcessSupervisor` now hooks `AppDomain.CurrentDomain.ProcessExit`
  to kill the current R process if the host exits without calling
  `Dispose()`. Documented clearly in the code and here: this covers an
  ordinary managed exit, **not** a hard kill (`kill -9`, Task Manager
  "End Process", a container force-stop, power loss) - those bypass
  all managed shutdown code on any platform, full stop. Proper
  coverage needs an OS-level mechanism (Windows Job Objects, Linux
  `prctl(PR_SET_PDEATHSIG)`) requiring P/Invoke - planned, not done
  (see phase-8 plan).
- **Answered "is `_connectionLock` enough for multi-threaded use?"
  with executable proof, not just prose**:
  `ConcurrentCalls_FromMultipleThreads_DoNotCorruptEachOther` and a
  sync+async-mixed variant in `ConcurrencyAndCancellationTests.cs`.
  Short answer: yes, for wire-protocol correctness - every method
  acquires `_connectionLock` before touching `_connection`, so
  concurrent callers queue rather than interleave frames. `State` and
  the diagnostic counters aren't independently synchronized, but
  that's an accepted, documented tradeoff (see that file's comments),
  not a correctness gap for the property that actually matters.
- **Answered "does cancellation corrupt the session / require a
  restart?" with executable proof**: yes to both, and that's correct,
  not a bug -
  `Cancellation_DuringInFlightCall_TriggersRestart_AndSupervisorRecovers`
  proves a cancelled in-flight call (a real `Sys.sleep(5)` call,
  genuinely interrupted mid-wait) triggers the same `Fault()`/restart
  path as a crash, and the supervisor recovers automatically -
  because a cancelled call can abandon the frame-based wire protocol
  mid-frame, with no safe way to resume alignment; discarding the
  connection is the only safe response.
  `Cancellation_BeforeAcquiringConnectionLock_DoesNotTriggerRestart`
  proves the complementary case: cancelling a call that's merely
  *waiting its turn* for the lock (nothing sent yet) does **not**
  trigger a restart, since `_connectionLock.WaitAsync(ct)` sits
  outside the fault-handling `try/catch` in every call method.
- **DTO stress testing**: `KitchenSinkDto` (every default-registered
  `RTypeConverter` type, nullable and non-nullable) plus
  `RTypeConverterStressTests.cs` - full-DTO round trip, MinValue/
  MaxValue edge cases for every numeric type (including two tests that
  deliberately assert the *documented* `long`/`decimal` precision-loss
  limitation rather than pretending it doesn't exist), empty/Unicode
  strings, a 10,000-element array, a 5,000-row `List<TRecord>` → TABLE
  → `List<TRecord>` round trip, and three-levels-deep nested objects.

- **Wrote `docs/phases/phase-8-plan.md`**: the forward-plan doc this
  file references, sequencing every still-pending item below into
  tiers (small/self-contained, needs-diff-writeup-first,
  needs-real-machine) with rationale for the ordering.
- **`IProcessSupervisor` interface + README usage docs**: extracted an
  interface (`IProcessSupervisor.cs`) mirroring `ProcessSupervisor`'s
  full public call surface (status/diagnostics properties, the
  `DiagnosticOutput` event, `StartAsync`, and all five
  Eval/Call/SetObj/GetObj/CreateRef operations in both sync and async
  form) so host application code can depend on the interface instead
  of the concrete class for its own unit tests. Deliberately scoped:
  `RHandle` still calls back into the concrete `ProcessSupervisor`
  internally regardless of which type a caller holds it through - this
  is a mockable call surface, not a full abstraction over internals.
  See `phase-8-plan.md`'s "IProcessSupervisor design notes" for the
  reasoning. README rewritten from its stale Phase-0 status line to
  reflect actual current status, plus a full usage section (basic
  Eval/Call/SetObj example, logging, fault/restart behavior, custom
  channels).
- **Wrote `docs/spec-deviations.md`**: every divergence between
  `spec.md` and the actual implementation, consolidated in one place
  and organized by spec section (rather than the session-by-session
  order it accumulated in). Distinguishes divergences that are fully
  **resolved** (spec.md itself has now been corrected to match, e.g.
  §4.2's PING/PONG split, §5.3's narrower factor encoding) from ones
  that are still **open** (most notably §6.2's TABLE streaming goal,
  still unimplemented after two deferrals, and §5.2's compact-only
  logical encoding, where spec.md was *also* out of date until this
  pass — spec.md §5.2 corrected as part of writing this doc, since it
  still described a negotiated wide/compact pair that was never
  built). This document is now what "record the change... rather than
  silently diverging" (top of this file) points to; the old inline
  "Decisions changed since spec.md" section has been trimmed to a
  pointer at it, so there's a single copy to keep in sync going
  forward.
- **`ProcessSupervisor` decomposition, partial**: extracted
  `ProcessSupervisorWireCodec` (the seven pure static encode/decode
  helpers) and `DiagnosticsBuffer` (the stdout/stderr ring buffer) out
  of `ProcessSupervisor.cs` — mechanical, zero behavior change,
  ~160 lines removed. The larger lifecycle/dispatch/supervision split
  was investigated and deliberately **not** attempted: it would touch
  a real synchronization subtlety (`_connectionLock` doesn't guard
  `_connection` during a restart — `State`/`_restartGate` do that
  instead) that's too easy to get wrong without a compiler/test runner
  to catch it. Full analysis and recommendation in
  `docs/phases/processsupervisor-decomposition.md`.
- **Real TABLE streaming design**: written up in full as
  `docs/phases/table-streaming-design.md` rather than attempted as a
  code change. Resolves the fork Phase 7 got stuck on (`IBufferWriter`
  flush-hook vs. `async Encode`) by observing neither is needed: a
  streaming `IBufferWriter` can write through in small bounded chunks
  without a flush hook, and the length-prefix problem is solved by
  running the existing, unchanged `Encode` twice (once to count bytes,
  once to stream them) instead of hand-maintaining a second length
  formula. Write side (C#→R) is designed in implementable detail; read
  side (R→C#) is flagged as needing its own follow-up pass since
  `RValueCodec.Decode` is structurally offset-based over an
  already-complete span, plus a concrete independent reason to
  prioritize it later (large-table receives likely aren't actually
  served from `ArrayPool.Shared`'s pooled buckets today — worth
  confirming on a real runtime).
- **Dockerfile for cross-platform (Linux) verification**, at repo
  root, plus a matching `.dockerignore`. Untested (no Docker/network
  access in this sandbox) — a well-informed first draft, not a
  confirmed-working image. Builds on `mcr.microsoft.com/dotnet/sdk:10.0`,
  installs `r-base-core` + CRAN `data.table`, then runs
  `dotnet build`/`dotnet test` against the whole solution so the
  Rscript-launching integration tests actually execute on Linux.

### First real test run — fixes from actual failures

The user ran `dotnet test` twice now. Round 1 (six failures): five
were genuine bugs, now fixed; the other two (both timing-sensitive
process/heartbeat tests) are very likely the long-tracked "flaky test"
finally caught with an actual explanation, addressed via test-suite
configuration rather than a production-code change - see below for why.


  with `TypeTag.Null` (e.g. a null `string`/`DateTime`/`TimeOnly`/
  `Guid` property) crashed with a `NullReferenceException` on decode
  whenever the target type had a *registered direct edge* (string,
  DateTime, Guid, etc.) - `ConvertObject` checks the direct-edge table
  before ever reaching the structural/Null-aware path
  (`TryStructuralConvert`/`ConvertFromRValue`), so the direct edge's
  own body (e.g. `v.CharacterValues![0]!`) ran against an
  `RValue.Null()`, which has no data in any of its arrays. Fixed by
  adding the Null check directly in `ConvertObject`, before the
  direct-edge lookup - the single place every conversion path actually
  goes through. Removed the now-unreachable duplicate check that
  previously lived in `ConvertFromRValue` (dead code once the earlier
  check exists) rather than leave two copies of the same logic to
  drift out of sync. This was `KitchenSinkDto_FullyPopulated_RoundTrips`'s
  `NullReferenceException` - `NullableStringValue = null` (plus three
  other null nullable properties of directly-edged types) triggered it.
- **Real bug, decimal conversion**: `(decimal)v.DoubleValues![0]`
  threw `OverflowException` for `decimal.MaxValue` - decimal has ~28-29
  significant digits, double only ~15-17, so `decimal.MaxValue` rounds
  to a double that lands at or just past decimal's actual representable
  range even though it's nowhere near double's own (much larger)
  magnitude ceiling. Fixed by clamping to `decimal.MaxValue`/
  `MinValue` when the double is at or beyond that boundary, consistent
  with how `long`/`ulong`/`uint` already document "rides the Double
  vector, loses precision beyond double's range" as expected behavior
  rather than a crash.
- **Real bug, DateTime conversion**: `unixEpoch.AddSeconds(seconds)`
  threw `ArgumentOutOfRangeException` for `DateTime.MaxValue` -
  floating-point rounding in the seconds-since-epoch double was enough
  to push the reconstructed value just past `DateTime.MaxValue`'s tick
  range, even though the original value was in range. Fixed by
  catching that specific exception and clamping to `DateTime.MinValue`/
  `MaxValue` (sign of the seconds value determines which) - the
  clamped result differs from the true value by less than the
  rounding error that caused the overflow in the first place, well
  inside the test's own 1ms tolerance.
- **Test bugs, not production bugs** (both in `RTypeConverterStressTests`):
  - `Long_ExtremeValues...`: the test manually reproduced an
    *unchecked* `(long)` cast on a double and asserted the result
    differs from `long.MaxValue` - but .NET's unchecked
    floating-point-to-integer conversions saturate (a real runtime
    behavior, not a bug), and for this exact boundary the saturated
    result coincidentally equals `long.MaxValue`, making the assertion
    fail for a reason unrelated to any real defect. Rewritten to
    assert what's actually true and meaningful: the *registered*
    converter uses a `checked` cast and correctly throws
    `OverflowException` for `long.MaxValue`.
  - `Decimal_ExtremeValues...`: previously asserted
    `decimal.MaxValue`'s round trip differs from the original - once
    the production clamp fix above lands, that value round-trips
    *exactly* (the clamp recovers it), so the assertion needed
    updating rather than the code. Rewritten to assert the clamp
    behavior directly (no throw, exact match at the boundary) plus a
    genuinely lossy case using a large decimal with real fractional
    precision beyond double's ~15-17 significant digits, which
    demonstrates actual precision loss without relying on
    boundary-clamp coincidence.
- **Likely resolved, the long-tracked "flaky test"**: two failures -
  `ConcurrencyAndCancellationTests.Cancellation_BeforeAcquiringConnectionLock_DoesNotTriggerRestart`
  and `ProcessSupervisorTests.ExternalProcessKill_TriggersAutomaticRestart_AndSupervisorRecovers`
  - both showed a supervisor state inconsistent with either test's
  timing assumptions (an unexpected `Restarting`; a kill never observed
  across a full 5-second poll window). Both tests use deliberately
  tight timing budgets (`HeartbeatInterval=300ms`,
  `HeartbeatResponseTimeout=2s`, restart backoff in the low hundreds of
  ms) to keep the suite fast, and **neither test class is in a shared
  xunit collection** - every test class in this project is its own
  implicit collection, and xunit parallelizes collections against each
  other by default. That means this suite, as configured, runs a
  dozen-plus classes' worth of real `Rscript` subprocesses, socket
  I/O, and heartbeat loops all at once - real, not simulated, system
  contention exactly capable of blowing through timing budgets sized
  for a quiet machine. This fits both failures precisely (a heartbeat
  round-trip or a `Process.Exited` callback delayed by scheduling
  contention, not a logic bug in `Fault()`/`OnProcessExited`, which
  were checked directly and look correct - `Process.Exited` is already
  wired for near-instant crash detection, independent of the
  heartbeat). Rather than loosen the tests' timing budgets (which
  would just mask the same underlying contention without fixing it, and
  make the suite slower for everyone) or touch verified restart/
  heartbeat logic on unreproduced evidence, added
  `tests/RWire.Tests/AssemblyInfo.cs` with
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]` -
  forces the whole suite to run one collection at a time, eliminating
  cross-class contention as a variable. **Not yet verified** (needs a
  real test run to confirm both failures don't recur) - if either
  still flakes with this in place, that's much stronger evidence of an
  actual production bug worth investigating with real timestamped
  NLog output, rather than environmental contention.

### Second real test run — two more fixes

After the round-1 fixes above, a second `dotnet test` run surfaced two
more failures. Same pattern: one genuine platform-level exception bug,
one wrong test assumption.

- **Real bug, `LaunchAsync`'s handshake timeout**: cancelling
  `_channelListener.AcceptAsync(linkedCts.Token)` via `timeoutCts`
  firing can surface as a raw `SocketException`
  (`SocketError.OperationAborted`, "I/O operation has been aborted
  because of either a thread exit or an application request") instead
  of a clean `OperationCanceledException` - a real, observed .NET/
  Windows platform quirk in how a cancellation-triggered abort of a
  pending `TcpListener.AcceptTcpClientAsync` completes, not something
  application code can prevent. The existing `catch
  (OperationCanceledException) when (timeoutCts.IsCancellationRequested)`
  only handled the well-behaved case, so the raw `SocketException`
  escaped uncaught instead of becoming the documented `TimeoutException`.
  Fixed by adding a parallel `catch (SocketException) when
  (timeoutCts.IsCancellationRequested)` right next to it - same
  handling, since `timeoutCts` firing makes the cause unambiguous
  either way. Applied to both `AcceptAsync`'s catch and
  `ReceiveAndValidateHelloAsync`'s catch (the HELLO-frame read), since
  the latter goes through `NetworkStream.ReadAsync` and could in
  principle hit the same class of quirk, even though this specific
  failure was only observed on the accept side. This was
  `DiagnosticOutput_CapturesOutput_FromWorkerScript`'s failure.
- **Test bug, not a production bug**: `Cancellation_BeforeAcquiringConnectionLock_DoesNotTriggerRestart`
  asserted the blocking call's result (`Sys.sleep(1)`) had
  `TypeTag.Double` - but `Sys.sleep()` in R always returns
  `invisible(NULL)`; that was never going to be a `Double`, regardless
  of anything RWire does. Fixed by changing the blocking expression to
  `"{ Sys.sleep(1); 42 }"` (still sleeps for a full second, holding
  the lock exactly as the test needs, but now returns something
  concrete) and asserting the actual value, not just a type that could
  never have matched. Worth noting: this test's OTHER assertion (the
  `State`/`RestartCount` checks that were failing in round 1) passed
  cleanly this time - some evidence, though not proof, that the
  `DisableTestParallelization` fix from round 1 is helping.

### Still pending from this feedback (see docs/phases/phase-8-plan.md)

- A runnable benchmark harness (BenchmarkDotNet or similar) for the
  user to execute and report results back, since this sandbox has
  never had R/.NET available to run one directly.
- `ProcessSupervisor` responsibility decomposition — **partially done**,
  see "Done this session" above and
  `docs/phases/processsupervisor-decomposition.md` for the full
  lifecycle/dispatch/supervision split, which is deliberately not
  attempted yet.
- `System.IO.Pipelines`-based rewrite of `RConnection`'s read/write
  path (the user specifically asked about Pipelines/Channels for
  read/write performance, beyond the socket-option tuning done this
  session).
- The real TABLE streaming work from Phase 7 — **design done**, see
  "Done this session" above and
  `docs/phases/table-streaming-design.md`. Implementation is not
  started; the write side (C#→R) is designed in full, the read side
  (R→C#) explicitly needs its own follow-up design pass.
- Cross-platform (Linux) verification — `Dockerfile` now written (see
  "Done this session" above), but not yet built/run anywhere; the
  actual verification still needs a machine with Docker.
- A project-wide `dotnet format`-style cleanup pass for the remaining
  IDE0300/IDE0301/CA1861-style suggestions beyond the specific lines
  fixed this session (these are cosmetic, not correctness issues -
  low priority relative to everything else above).
- The specific flaky test(s) - see "First real test run" above for the
  two prime suspects and the mitigation applied
  (`DisableTestParallelization`). Needs one more real test run to
  confirm it actually resolves them before this can be marked closed.

## Locked-in decisions (see spec.md for full detail/rationale)

- Name: **RWire**. Targets: R ≥ 4.4, .NET 10.
- Transport v1: TCP loopback socket; architecture is channel-agnostic
  (`IRChannel` for the data channel, `IRChannelListener` for
  establishing it) so named pipes / memory-mapped files can be added
  later without protocol changes.
- Both sync and async C# call paths, implemented as genuinely
  independent execution loops over the same frame codec — never one
  faked on top of the other.
- Custom binary protocol for everything, control plane and data plane.
  **Apache Arrow / Arrow Flight evaluated and rejected** — see
  spec.md §6.4 for why, so this doesn't get re-litigated mid-
  implementation.
- R packages are an accepted dependency; `data.table` is used on the R
  side. No custom C code authored/maintained for R, though.
- `TABLE` is a first-class wire type for data.frame/data.table-shaped
  data — not routed through generic serialize(). This is the answer to
  "how do we move a 10M-row × 1000-column table fast" — though the
  actual zero-buffer streaming part of that answer is still deferred
  to Phase 7 (see below).
- NA handling is bit-level (R's actual sentinel patterns), done in a
  low-level decoder step; conversion to idiomatic nullable C# types
  happens at a higher layer, not inside the hot decode loop.
- `RTypeConverter`/`RValueConversionExtensions` (class/collection ↔
  `RValue` mapping with `Register`/chaining) is a separate, optional
  convenience layer over `RValue` — not part of the wire protocol, not
  a substitute for direct `RValue` construction on the performance-
  critical bulk-transfer path.

## Phase checklist

- [x] Phase 0 — Skeleton & handshake (superseded by Phase 1's real handshake)
- [x] Phase 1 — Channel abstraction & frame protocol — **confirmed working** (manual build/test)
- [x] Phase 2 — Atomic type mapping (hot path) — **confirmed working**
- [x] Phase 3 — Reference counting — **confirmed working**
- [x] Phase 4 — `TABLE` type & bulk transfer — **confirmed working** (streaming optimization still deferred to Phase 7 — see below)
- [x] Phase 5 — Cold path (serialize/unserialize) + irregular objects — **confirmed working**
- [x] Phase 6 — Process supervision & resilience — **confirmed working**
- [ ] Phase 7 — Performance hardening (partially implemented — see phase doc; TABLE streaming design now done, see docs/phases/table-streaming-design.md; implementation and C-rewrite profiling still open)
- [ ] Phase 8 — Feedback-driven hardening (in progress — see "Current phase" above and docs/phases/phase-8-plan.md)

Each phase's detail doc has its own finer-grained checklist. "Confirmed
working" means the user has built and run it manually — not just that
it compiled during implementation.

## Decisions changed since spec.md was written

Moved to `docs/spec-deviations.md`, organized by spec section instead
of chronologically, and marked resolved/open per item. Do not add new
entries here — add them there.

## Notes / blockers

- **Phase 7's changes have not been built or run.** They're narrower
  and lower-risk than Phase 6's rewrite (internal buffer-handling only
  in `RConnection.cs`, a return-type change on two `private` methods
  in `ProcessSupervisor.cs`, and otherwise entirely new test files) —
  but "lower risk" isn't "verified." Priority order: (1) `dotnet
  build`; (2) `RConnectionTests`' two new large-payload tests
  specifically, since they're the most likely to reveal a mistake in
  the header/payload split; (3) confirm every *existing* integration
  test still passes unchanged, since Phase 7 should not have altered
  any observable behavior; (4) run the new benchmark tests and
  actually read the numbers.
- **TABLE is still not actually streamed** (see "Decisions changed"
  above) — this is now a twice-deferred, well-understood gap with a
  documented reason and a documented next step
  (`docs/phases/phase-7-performance-hardening.md`), not something to
  re-discover as a surprise. `TablePerformanceTests`' timings remain
  logged, not asserted against a threshold.
- **C# never gets a deserializer for R's `serialize()` format**
  (Phase 5) — deliberate, permanent scope boundary, not a gap.
  `RValue.SerializedBytes` is meant to be shuttled between R calls
  unexamined.
- The wire shape (frame format, RValue types, Table, registry/handle
  IDs) is still hand-kept in sync between `worker.R` and the C# side
  across several files, with no shared source of truth between the
  two languages. Still worth codegen if a future phase has room for
  it — the surface has grown substantially across Phases 2–7.
- `RTypeConverter`'s reflection-based property discovery relies on
  .NET returning properties in declaration order (true in practice for
  ordinary Roslyn-compiled classes, not a hard CLR guarantee) for
  several test assertions that check a specific `Names` order. If that
  ever turns out flaky across environments, sort by `MetadataToken`
  explicitly instead of relying on default reflection order.

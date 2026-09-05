# RWire — Progress Tracker

**How to resume this project in a new session (human or AI):**
1. Read this file first — it says which phase is current and what's
   actually done vs. still open.
2. Open `docs/spec.md` for the full locked-in design (architecture,
   protocol, type mapping, table format, rationale for rejected
   alternatives like Arrow).
3. Open `docs/phases/phase-N-*.md` for the current phase — each one is
   self-contained: goal, prerequisites, a concrete checklist, files to
   touch, and exit criteria, written so it doesn't require re-reading
   the whole conversation history that produced it.
4. Update the checklist and the "Current phase" line below as you go —
   this file is the source of truth for where things stand, not chat
   history.

Do not re-derive design decisions already settled in `spec.md` —
if something there seems wrong once implementation starts, record the
change in "Decisions changed since spec" below rather than silently
diverging.

---

## Current phase

**Phase 1 — Channel abstraction & frame protocol** (implementation
written, not yet built/run — see "Notes / blockers" below)

Phase 0 (skeleton & handshake) is superseded by Phase 1's real
protocol — the placeholder handshake described in the original Phase 0
notes no longer exists in the code; `ProcessSupervisor`/`worker.R` now
do the real `HELLO` exchange described below.

Implemented:
- `src/RWire/IRChannel.cs` — transport interface, dual sync/async,
  neither derived from the other.
- `src/RWire/SocketRChannel.cs` — the v1 (and so far only) `IRChannel`
  implementation, over `NetworkStream`.
- `src/RWire/MsgType.cs` — wire message type enum (see "Decisions
  changed since spec.md" below — values differ slightly from the
  original spec table, which is now corrected to match).
- `src/RWire/FrameCodec.cs` — pure `Span<byte>`-based encode/decode,
  no transport dependency; cross-checks `PayloadLen` against the
  outer `Length` field on decode and throws `InvalidDataException` on
  any inconsistency or unknown `MsgType` byte.
- `src/RWire/Frame.cs` — decoded frame wrapper, payload buffer rented
  from `ArrayPool<byte>.Shared`, returned on `Dispose()`.
- `src/RWire/RConnection.cs` — `Send`/`Receive` (sync) and
  `SendAsync`/`ReceiveAsync` (async) built from `IRChannel` +
  `FrameCodec`; both paths implemented independently, sharing no
  execution code, only the same wire format.
- `src/RWire/ProcessSupervisor.cs` — rewritten: real `HELLO` handshake
  (token + `R.version.string`, both length-prefixed UTF-8 strings) via
  `RConnection`; `PeriodicTimer`-driven heartbeat (`PING`/`PONG`) that
  skips a tick rather than blocking if an application call already
  holds the connection lock; `Dispose()` now sends a real `SHUTDOWN`
  frame before the existing close-socket/grace-period/force-kill
  sequence.
- `r/worker.R` — rewritten: real frame read/write functions, sends
  `HELLO` on connect, message loop dispatches `PING`→`PONG` and
  `SHUTDOWN`→`RESULT`+exit; every other `MsgType` gets a well-formed
  `ERROR` response (not implemented until Phases 2–3) rather than
  hanging, crashing, or being silently dropped; per-request R errors
  are caught and turned into `ERROR` frames without ending the loop.
- Tests: `FrameCodecTests.cs` (pure unit, round-trip + corruption
  cases), `RConnectionTests.cs` (loopback-socket unit tests, no R
  process — round-trip, zero-length payload, closed-channel EOF,
  correlation ID increment), `ProcessSupervisorTests.cs` (rewritten:
  handshake now asserts `RVersion` populated; added heartbeat-stays-
  Ready, external-kill-detected-as-Faulted, and graceful-shutdown-
  clean-exit-code tests).
- `src/RWire/AssemblyInfo.cs` — `InternalsVisibleTo("RWire.Tests")` so
  tests can reach `ProcessSupervisor.ProcessForTesting` (needed to
  simulate an external kill) without making it public API.

Not yet implemented (by design — later phases):
- `EXEC`/`EVAL`/`CALL`/`GET_OBJ`/`SET_OBJ`/`CREATE_REF`/`RELEASE_REF`
  — all defined in `MsgType` but stubbed to return `ERROR` from
  `worker.R`'s dispatcher (Phases 2–3).
- Full `SupervisorState` state machine (`Restarting`, backoff) — only
  `NotStarted`/`Starting`/`Ready`/`Faulted`/`Disposed` exist.
- `System.IO.Pipelines` — `RConnection`'s async path currently reads
  directly off `IRChannel.ReadAsync` rather than through
  `PipeReader`; spec §9 leaves this as a later optimization to
  benchmark (Phase 7), not a Phase 1 requirement.

## Locked-in decisions (see spec.md for full detail/rationale)

- Name: **RWire**. Targets: R ≥ 4.4, .NET 10.
- Transport v1: TCP loopback socket; architecture is channel-agnostic
  (`IRChannel`) so named pipes / memory-mapped files can be added later
  without protocol changes.
- Both sync and async C# call paths, implemented as genuinely
  independent execution loops over the same frame codec — never one
  faked on top of the other.
- Custom binary protocol for everything, control plane and data plane.
  **Apache Arrow / Arrow Flight evaluated and rejected** — see
  spec.md §6.4 for why, so this doesn't get re-litigated mid-
  implementation.
- R packages are an accepted dependency; `data.table` will be used on
  the R side. No custom C code authored/maintained for R, though.
- `TABLE` is a first-class wire type for data.frame/data.table-shaped
  data — not routed through generic serialize(). This is the answer to
  "how do we move a 10M-row × 1000-column table fast."
- NA handling is bit-level (R's actual sentinel patterns), done in a
  low-level decoder step; conversion to idiomatic nullable C# types
  happens at a higher layer, not inside the hot decode loop.

## Phase checklist

- [x] Phase 0 — Skeleton & handshake (superseded by Phase 1's real handshake — see above)
- [ ] Phase 1 — Channel abstraction & frame protocol (code written, unbuilt — see current-phase notes)
- [ ] Phase 2 — Atomic type mapping (hot path)
- [ ] Phase 3 — Reference counting
- [ ] Phase 4 — `TABLE` type & bulk transfer
- [ ] Phase 5 — Cold path (serialize/unserialize) + irregular lists
- [ ] Phase 6 — Process supervision & resilience
- [ ] Phase 7 — Performance hardening

Each phase's detail doc has its own finer-grained checklist — this
top-level one is just for at-a-glance status. None of these get a
final checkmark until `dotnet test` has actually passed against real
R/.NET installations — see "Notes / blockers."

## Decisions changed since spec.md was written

- **PING/PONG split into distinct MsgType codes.** The original spec
  table listed them sharing value `0x02` as a documentation
  shorthand for "the heartbeat pair" — not workable as an actual
  wire value, since they're different frames in opposite directions.
  Implemented as `PING = 0x02`, `PONG = 0x03`, with every subsequent
  `MsgType` value shifted up by one relative to the original table.
  `docs/spec.md` §4.2 has been corrected to match; `src/RWire/MsgType.cs`
  is the source of truth for numbering going forward.

## Notes / blockers

- All code so far (Phases 0 and 1) was written in an environment with
  **no .NET SDK and no R installation available**, so `dotnet build` /
  `dotnet test` have **not** been run against any of it yet. Treat it
  as a solid draft matching the relevant checklists, not as
  verified-working code. First thing to do on a machine with both
  toolchains installed:
  1. `dotnet build` — fix whatever compiler errors turn up (most
     likely candidates: a `using`/namespace nit, or a .NET 10 API
     shape difference between preview/RC builds).
  2. `dotnet test` — start with `FrameCodecTests` and
     `RConnectionTests` (pure C#/loopback-socket, no R dependency) to
     isolate protocol-layer bugs before debugging anything that
     involves the R process.
  3. Then `ProcessSupervisorTests` (needs `Rscript` on PATH) — the
     heartbeat and external-kill tests have real timing assumptions
     (`HeartbeatInterval`/`HeartbeatResponseTimeout` in the hundreds
     of ms to a few seconds) that may need loosening on a slower CI
     machine if they're flaky.
- `worker.R`'s frame constants (`MSG_HELLO`, `MSG_PING`, etc.) are
  hand-kept in sync with `src/RWire/MsgType.cs` — there's no shared
  source of truth between the two languages yet. If a future phase
  adds a code-generation step for this, note it here; until then,
  changing one without the other is a real way to silently break the
  protocol.

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

**Phase 3 — Reference counting** (implementation written, not yet
built/run — see "Notes / blockers" below)

Phase 2 status: still "code written, unbuilt" as of this update — it
was not built/tested before Phase 3 work started. Build and test
Phase 2 and Phase 3 together; there's no meaningful checkpoint between
them anymore since Phase 3 builds directly on Phase 2's RValueCodec.

Phase 3 adds, on top of Phase 2:

Implemented:
- `src/RWire/RHandle.cs` — thin `IDisposable` proxy (not `SafeHandle` —
  see "Decisions changed since spec.md") stamped with the owning
  `ProcessSupervisor`'s `SessionId`; `Dispose()` calls
  `ReleaseHandleBestEffort` (fire-and-forget, exception-swallowing —
  Dispose must never throw); a finalizer exists as the last-resort net
  spec.md describes, not the primary path.
- `src/RWire/RCallArgument.cs` — discriminated union (inline `RValue`
  or `RHandle`) with implicit conversions from both, so
  `Call`/`CallAsync` call sites rarely need to construct it explicitly.
  This **changed the public signature** of `Call`/`CallAsync` from
  `IReadOnlyList<RValue>` to `IReadOnlyList<RCallArgument>` — a source
  break from Phase 2, fixed up in `EvalCallIntegrationTests.cs`.
- `src/RWire/ProcessSupervisor.cs` — added:
  - `SessionId` (a process-wide monotonic counter via
    `Interlocked.Increment`, stamped on every `RHandle` created by
    this instance) and `ValidateHandle` (throws `ObjectDisposedException`
    if a handle's `SessionId` doesn't match — stands in for "this
    handle survived a restart it shouldn't have," ready for Phase 6
    even though restart doesn't exist yet).
  - `SetObj`/`SetObjAsync`, `GetObj`/`GetObjAsync`,
    `CreateRef`/`CreateRefAsync` (sync+async, same pattern as
    `Eval`/`Call`: `RErrorException` restores `Ready`, everything else
    faults). `Call`/`CallAsync` updated to validate and resolve any
    handle arguments *before* acquiring the connection lock / entering
    `Busy` — an already-disposed handle is a client bug and must throw
    `ObjectDisposedException` without touching supervisor state, not
    get treated as a connection failure.
  - `ReleaseRefAsync` (internal) and `ReleaseHandleBestEffort` — the
    actual RELEASE_REF call and the fire-and-forget wrapper
    RHandle.Dispose/finalizer use.
  - Wire helpers `EncodeHandleId`/`DecodeHandleIdResult` (8-byte LE
    handle IDs) and `EnsureSuccessAck` (RESULT-with-empty-payload =
    success, shared by CREATE_REF/RELEASE_REF).
- `r/worker.R` — the object registry (`.rwire_registry`, an
  `environment()`-as-hashtable), handle ID allocation (see "Decisions
  changed since spec.md" — 32-bit, not the full 64-bit wire slot),
  `SET_OBJ`/`GET_OBJ`/`CREATE_REF`/`RELEASE_REF` handlers, the
  leak-guard sweep (piggybacked on the existing `PING` handler rather
  than a separate timer), and `CALL`'s handle-argument branch now
  actually resolves via the registry instead of throwing "not
  implemented."
- Tests: `HandleLifecycleTests.cs` covering the full spec §12.4 list
  (set→get round-trip, dispose→registry-empty, use-after-dispose
  throws, two-handles-via-CreateRef, double-release-is-a-no-op) plus a
  session-mismatch test standing in for the "old handle after a crash/
  restart" scenario (using a second, independent `ProcessSupervisor`
  instance, since Phase 6's actual restart doesn't exist yet — this
  tests the piece Phase 3 owns: the `SessionId` check itself, not the
  restart machinery around it). `EvalCallIntegrationTests.cs` updated
  for the `RCallArgument` signature change and given a new
  handle-as-CALL-argument test.

Not yet implemented (by design — later phases):
- `TABLE` type (Phase 4).
- Full crash/restart integration with the handle registry — Phase 6
  needs to actually invalidate *all* outstanding handles from a
  session when that session's process is replaced; today `SessionId`
  only prevents a handle from a genuinely different `ProcessSupervisor`
  instance from being misused, which is the right primitive but isn't
  wired into any restart logic yet because there isn't any.

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

- [x] Phase 0 — Skeleton & handshake (superseded by Phase 1's real handshake)
- [~] Phase 1 — Channel abstraction & frame protocol (builds; tests not yet confirmed passing)
- [ ] Phase 2 — Atomic type mapping (hot path) (code written, unbuilt)
- [ ] Phase 3 — Reference counting (code written, unbuilt)
- [ ] Phase 4 — `TABLE` type & bulk transfer
- [ ] Phase 5 — Cold path (serialize/unserialize) + irregular lists
- [ ] Phase 6 — Process supervision & resilience
- [ ] Phase 7 — Performance hardening

Each phase's detail doc has its own finer-grained checklist — this
top-level one is just for at-a-glance status. `[~]` means "builds but
not test-verified"; nothing gets a plain `[x]` until `dotnet test`
has actually passed for that phase's suite.

## Decisions changed since spec.md was written

- **PING/PONG split into distinct MsgType codes.** The original spec
  table listed them sharing value `0x02` as a documentation
  shorthand for "the heartbeat pair" — not workable as an actual
  wire value, since they're different frames in opposite directions.
  Implemented as `PING = 0x02`, `PONG = 0x03`, with every subsequent
  `MsgType` value shifted up by one relative to the original table.
  `docs/spec.md` §4.2 has been corrected to match; `src/RWire/MsgType.cs`
  is the source of truth for numbering going forward.

- **Logical vectors: only the compact (1-byte) wire encoding was
  implemented, not the wide (4-byte)/compact negotiated pair spec
  §5.2 describes.** The per-message size-based negotiation is a
  performance optimization with no observable difference until
  benchmarking (Phase 7) actually shows the 1-byte-per-element remap
  cost matters at scale. Implementing both forms now (with a flag
  byte to signal which) would have added real complexity for a
  question Phase 2 has no data to answer yet. `RValueCodec` uses
  compact-only; revisit in Phase 7 if benchmarks justify it, and
  update this entry (or remove it) at that point.

- **Factor encoding is narrower than spec §5.3 originally described.**
  Only `class` is fast-pathed (via the existing Class header slot);
  `levels` rides the generic attribute block as a single recursive
  `RValue` entry rather than getting its own dedicated wire slot.
  `docs/spec.md` §5.3 has been corrected to describe this as the
  actual design rather than a fully bespoke factor shape — the
  practical win (avoiding the slow path for the attribute that's
  actually common) is already captured without the added complexity
  of a fully separate factor wire format.

- **RHandle is a plain `IDisposable` class with a finalizer, not
  `SafeHandle`.** Phase 3's planning doc left this open explicitly.
  `SafeHandle` is designed around wrapping a native/unmanaged handle
  value with OS-level semantics (`IsInvalid`, `ReleaseHandle` running
  on a special reliability path); RWire's handle is a logical 64-bit
  ID with no OS resource behind it. The plain-class approach with an
  explicit `Dispose()` as the primary path and a finalizer as a
  documented last resort gets the same safety properties spec.md
  section 8 asks for without inheriting `SafeHandle`'s native-interop-
  shaped API.

- **Double-release is a no-op, not an error.** Also left open by the
  Phase 3 plan. `rwire_registry_release` on an already-gone key simply
  returns rather than raising a condition — a client-side double-
  release (Dispose racing a finalizer, or a caller disposing twice by
  mistake) is a normal, harmless occurrence and turning it into a
  protocol-level error would make defensive `Dispose()` patterns
  actively dangerous. `HandleLifecycleTests.DoubleRelease_IsANoOp_NotAnError`
  tests this directly.

- **Handle IDs are allocated as 32-bit R integers, not the full 64-bit
  range the wire format's 8-byte slot implies.** Base R has no native
  64-bit integer type without the `bit64` package, and generating IDs
  as R integers means R's own overflow behavior naturally caps the
  practical range at ~2 billion objects per session — far more than
  any realistic session needs. The wire slot is still 8 bytes (high
  4 bytes always zero, and required to be zero on read) so nothing
  about the frame format needs to change if a future need for the
  full range ever appears; only the R-side allocator would need to
  change. See `r/worker.R`'s `write_handle_id`/`read_handle_id`.

- **A disposed-handle mistake is validated and thrown *before*
  acquiring the connection lock / entering `Busy` state**, in
  `GetObj(Async)`, `CreateRef(Async)`, and `Call(Async)`'s handle
  arguments. This wasn't called out explicitly in the Phase 3 plan but
  is a direct consequence of spec.md's own non-fatal-error principle
  (section 12.5): using an already-disposed `RHandle` is a client
  programming error, not a connection or protocol failure, and must
  not fault the supervisor the way a genuine wire-level problem would.

## Notes / blockers

- Phase 1's code **builds successfully** (confirmed on a real machine).
  Its tests have not been confirmed passing yet.
- Phases 2 and 3's code has **not been built at all**. Build order:
  1. `dotnet build` — Phase 3 touched `Call`/`CallAsync`'s public
     signature (now `IReadOnlyList<RCallArgument>`), so a Phase 2-only
     partial build isn't meaningful; build everything together.
  2. `RValueCodecTests` (pure C#) — as before, run
     `Double_NaReal_And_ComputedNaN_StayDistinct_AfterRoundTrip` first.
  3. `EvalCallIntegrationTests` and `HandleLifecycleTests` (need
     `Rscript`) — `HandleLifecycleTests` depends on `EvalAsync` working
     correctly (it uses `EvalAsync("exists(...)")` as a diagnostic
     probe into the R-side registry), so if `EvalCallIntegrationTests`
     is failing, fix that before debugging `HandleLifecycleTests`.
- `worker.R`'s registry functions (`rwire_registry_*`) have not been
  checked against a real R interpreter. The riskiest assumption:
  `assign()`/`get()` on a `list(value=..., refcount=..., last_touched=...)`
  stored by string key in an `environment()` — this is a standard R
  pattern but hasn't been exercised here. If `HandleLifecycleTests`
  fails oddly, check this before suspecting the wire protocol.
- The `HandleLifecycleTests` tests that poll for the background
  `ReleaseHandleBestEffort` `Task.Run` to complete
  (`Dispose_ReleasesTheHandle_FromTheRSideRegistry`,
  `TwoHandlesViaCreateRef_BothMustBeDisposed_BeforeObjectIsFreed`) have
  a real timing dependency (polling with a 2-second deadline) — if
  these are flaky on a slow machine, that's a test tuning issue, not
  necessarily a correctness bug; check the non-timing assertions in
  the same test first.
- `worker.R`'s frame *and* RValue *and* now registry/handle-ID wire
  shape are still hand-kept in sync with the C# side across three
  files (`MsgType.cs`/`RTypeTag.cs`/`RValueCodec.cs`) and this doc's
  own description of the handle ID encoding. This is the same
  no-shared-source-of-truth risk flagged after Phase 2, now with more
  surface area.

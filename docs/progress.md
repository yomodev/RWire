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

**Phase 4 — TABLE type & bulk transfer** (implementation written, not
yet built/run) **plus a large unplanned hardening pass** requested
directly against real build/test output — this is the first point in
the project where actual `dotnet build`/`dotnet test` results (not
just code review) drove changes. See below.

### Phase 4 itself

- `RTypeTag.Table`, `RValue.RowCount`/`OfTable`/`FromWireTable`/
  `GetTableColumns()`, and `RValueCodec` encode/decode for it. Table
  reuses the existing List shape (element count = column count,
  Names = column names, Class = R class) plus one extra `RowCount`
  int32 written right after the type tag - see spec.md section 6.2.
- `worker.R`: `is.data.frame(x)` gets its own branch in `write_r_value`
  *before* the generic `is.list(x)` check (a data.frame is a list, so
  order matters) that deliberately does **not** reuse the shared
  `write_attributes()` helper - `dim(x)` for a data.frame invokes the
  `dim.data.frame` S3 *method* (computed from nrow/ncol) rather than
  reading a literal stored attribute, and blindly round-tripping that
  would attach a spurious literal `dim` attribute on reconstruction.
  `read_r_value`'s `RTAG_TABLE` branch sets `row.names` explicitly via
  `attr<-` (bypassing the validating `row.names<-.data.frame` method,
  since the state being set is already known-correct) and calls
  `data.table::setDT()` when the class includes `"data.table"`.
- **Not implemented**: the actual zero-copy/streaming-without-a-
  precomputed-buffer optimization spec.md section 6.2 describes as
  the point of the TABLE type. Both C# (`ArrayBufferWriter`) and R
  (`rawConnection`) still buffer the fully-encoded value before it
  goes out over `RConnection.Send`/`write_frame`. What's implemented
  is TABLE's wire *format* and full round-trip correctness (a real
  win over encoding a data.frame as a generic list of columns, which
  is what happened before this phase) - not yet the bulk-transfer
  performance property that was the entire point of designing it.
  Making that real requires restructuring `RConnection.Send`/
  `write_frame` to accept a streaming-write callback instead of a
  pre-built buffer, on both sides - a distinct, larger task, most
  naturally tackled in Phase 7.
- List-of-tables (spec.md section 6.3) works as a side effect of
  Table being just another `RValue` - a `List` containing `Table`
  elements round-trips correctly (tested in both
  `RValueCodecTests.Table_ListOfTables_RoundTrips` and
  `TablePerformanceTests`). The `IAsyncEnumerable<RTable>`-based lazy,
  consume-while-still-arriving API spec.md describes is **not**
  implemented - the whole-buffer architecture above means "table N+1
  starts decoding while table N is still being consumed" isn't
  possible yet without that same streaming rework.

### Bug fixes from real test output

Three failures came from an actual `dotnet test` run against Phases
1-3 (first time this project has run against a real compiler/test
runner). All three are fixed:

1. **`EnsureReady()` rejected the transient `Busy` state.** A
   background `ReleaseHandleBestEffort` call and a foreground
   diagnostic `EvalAsync` raced; the foreground call checked `State`
   before queuing on `_connectionLock` and threw
   `InvalidOperationException` because it observed `Busy`. Fixed:
   `EnsureReady()` now only rejects genuinely unusable states
   (`Faulted`, `Disposed`, pre-`Ready`) - `Busy` means "someone else
   is using the connection right now," which is exactly what
   `_connectionLock` exists to serialize, not a reason to refuse a
   legitimate concurrent caller.
2. **`Dispose_SendsGracefulShutdown_AndProcessExitsCleanly` threw "No
   process is associated with this object."** The test read
   `Process.HasExited`/`ExitCode` *after* `supervisor.Dispose()`,
   which itself calls `_process.Dispose()` - .NET's `Process` throws
   on property access once disposed. Fixed: `ProcessSupervisor` now
   exposes `public int? ExitCode { get; }`, captured right before
   `_process.Dispose()` runs, so callers/tests never need to touch
   the underlying `Process` after `Dispose()`.
3. **`DiagnosticOutput_CapturesStderr_OnWorkerScriptError` was flaky**
   with the old `BeginOutputReadLine`/`OutputDataReceived` event
   pattern and a fixed `Task.Delay(200)`. Fixed as part of the async
   stdio rewrite below - the test now polls instead of guessing a
   delay, and the pump tasks start immediately after `Process.Start()`
   rather than depending on the event-loop's own internal timing.

### Architecture changes requested directly (not from spec.md)

- **Connection creation is now dependency-injected.**
  `IRChannelListener` (new) abstracts "bind + accept the R worker's
  connect-back" the way `IRChannel` already abstracted "read/write
  once connected." `ProcessSupervisor` takes an `IRChannelListener` in
  its constructor (defaulting to the new `TcpRChannelListener` if you
  use the single-argument constructor) and never touches
  `TcpListener`/`TcpClient` directly anymore. This was a real gap:
  Phase 1's channel-agnostic goal covered the data channel
  (`IRChannel`) but `ProcessSupervisor` still hardcoded the listener
  side of establishing that channel.
- **Errors are confirmed structured, and made richer.** They were
  already sent over the protocol's data channel (never stdout/stderr)
  before this change, but only as a bare message string.
  `RErrorException` now carries `Classes` (R's condition class
  hierarchy) and `Call` (the deparsed call, if R attached one), and
  `worker.R`'s `build_error_payload` accepts a real condition object
  (or wraps a plain string in `simpleError()` for protocol-level
  errors that aren't real R conditions) instead of just
  `conditionMessage(e)`. All four ERROR-decode call sites in
  `ProcessSupervisor` were consolidated into one `DecodeError` helper.
- **Stdout/stderr capture rewritten as async pump tasks.**
  `Process.BeginOutputReadLine()`/`OutputDataReceived` replaced with
  two `PumpStreamAsync` loops (`StandardOutput.ReadLineAsync()`/
  `StandardError.ReadLineAsync()`), started immediately after
  `Process.Start()`. `Dispose()` awaits both (with a timeout) after
  confirming the process has exited, so `DiagnosticOutput` is
  guaranteed to have seen every line by the time `Dispose()` returns -
  no more guessing with a fixed delay.
- **Test fixtures added**: `RWireProcessFixture` +
  `RWireProcessCollection` (xUnit collection fixture) share one
  R process across `EvalCallIntegrationTests`, `HandleLifecycleTests`,
  and `TablePerformanceTests` - none of those tests dispose or
  otherwise disrupt the shared supervisor's lifecycle, they only make
  ordinary calls against it. `ProcessSupervisorTests` (handshake
  failure, external kill, graceful shutdown) deliberately keeps
  per-test isolated instances, since those tests need to control a
  full process lifecycle themselves.
- **AwesomeAssertions** replaces raw `Assert.*` across every test file
  (`.Should()`-style fluent assertions).
- **`RandomTableGenerator`** (test-only) builds a table with one
  column of every supported atomic type (logical/integer/double/
  character/raw, each with a configurable NA-injection probability
  where the type has an NA concept) and mixed lists combining tables
  with plain vectors. **`TablePerformanceTests`** is a `[Theory]`
  sweep over row counts (100 / 1,000 / 10,000 / 100,000) plus mixed-
  list sizes, asserting correctness and logging (not gating on) timing
  via `ITestOutputHelper` - there's no reference machine here to set a
  meaningful pass/fail threshold against, and Phase 4's current
  whole-buffer implementation isn't the design the timings would
  ultimately need to be judged against anyway (see above). Read the
  logged numbers once you can actually run this; don't treat the tests
  passing as a performance claim.
- **`testthat` added on the R side** (`r/tests/testthat.R` +
  `r/tests/testthat/*.R`), confirmed acceptable. Required one small
  change to `worker.R` itself: the final `tryCatch(main(...), ...)`
  call is now guarded by
  `if (!isTRUE(getOption("rwire.testing", FALSE)))`, so the script's
  function definitions can be `source()`d for testing without
  immediately trying to connect as a live worker process.
  `test-value-codec.R`, `test-frame-codec.R`, and `test-registry.R`
  mirror the equivalent C# unit test files' coverage using
  `rawConnection` instead of a real socket - no C# process involved.

### The "rewrite the R side in C" question

Asked directly, answered in prose rather than code: **don't expect
much**, and this is a genuine judgment call, not a benchmarked number.
R's `writeBin`/`readBin` already execute in C internally for the
vectorized types (integer/double/raw/logical) that dominate large
TABLE transfers - a rewrite would only remove R-interpreter dispatch
overhead from the *control-plane* logic (function call overhead, S3
dispatch, environment/registry lookups via `assign()`/`get()`), which
is a small fraction of total time for anything payload-heavy. Rough,
unverified guess: low single digits of percent for large-table
transfers (bound by memcpy/socket throughput either way), possibly
noticeably more - maybe 10-20% - for a workload dominated by many
small, frequent calls where interpreter dispatch is a bigger fraction
of each call's total time. Real profiling (Phase 7) would be needed
before trusting either number; this shouldn't be read as a case either
for or against a C rewrite on its own.

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
- [~] Phase 1 — Channel abstraction & frame protocol (builds; some bugs found/fixed via real test output, not all re-verified since)
- [ ] Phase 2 — Atomic type mapping (hot path) (code written, unbuilt)
- [ ] Phase 3 — Reference counting (code written, unbuilt)
- [ ] Phase 4 — `TABLE` type & bulk transfer (code written, unbuilt; streaming optimization deferred - see above)
- [ ] Phase 5 — Cold path (serialize/unserialize) + irregular lists
- [ ] Phase 6 — Process supervision & resilience
- [ ] Phase 7 — Performance hardening (now also owns: real TABLE streaming, and validating the "rewrite in C" question with actual profiling)

Each phase's detail doc has its own finer-grained checklist — this
top-level one is just for at-a-glance status. `[~]` means "builds but
not fully test-verified"; nothing gets a plain `[x]` until
`dotnet test` has actually passed in full for that phase's suite.

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

- **TABLE's zero-copy/streaming goal (spec.md section 6.2) is not yet
  implemented.** The wire *format* is faithful to the spec (schema
  fast-pathed via the existing Names/Class attributes, one extra
  RowCount field), but both sides still buffer the whole encoded value
  in memory (`ArrayBufferWriter` in C#, `rawConnection` in R) before
  it goes out over the wire. This is the single biggest gap between
  what's implemented and what spec.md originally described as the
  point of designing TABLE at all — see "Current phase" above for the
  full reasoning and what changing it would require.

- **`ProcessSupervisor` now depends on `IRChannelListener`, not a
  concrete `TcpListener`, for the connection-establishment side of the
  channel abstraction.** Phase 1 already made the post-connection data
  channel pluggable via `IRChannel`; this closes the equivalent gap on
  the "how do we get connected in the first place" side. Not something
  the original phase plans called for explicitly, but a direct,
  correct extension of the same channel-agnostic principle spec.md
  section 2.1 already established.

- **Async stdio pump tasks replace the `BeginOutputReadLine`/
  `OutputDataReceived` event pattern.** Not a spec.md decision, but a
  concrete fix: `Dispose()` can now deterministically await both
  streams fully draining (with a timeout) rather than a test needing
  to guess a delay long enough for the old event-based mechanism to
  have caught up.

- **`RErrorException` carries structured `Classes`/`Call` fields, not
  just a message.** The error was already sent as a real object over
  the wire protocol before this change (never inferred from stdout/
  stderr) — this made that object more useful, matching what a real R
  condition actually carries.

## Notes / blockers

- **This is the first phase where real `dotnet build`/`dotnet test`
  output (not just code review) drove changes** - three concrete bugs
  were found and fixed this way (see "Current phase" above). Treat
  this as evidence the earlier "builds successfully" status for
  Phase 1 meant exactly that and no more - compiling clean does not
  mean behaviorally correct, and the same is almost certainly true of
  Phases 2-4's code below, which has not been run at all yet.
- Phases 2, 3, and 4's code (including everything added in this
  hardening pass) has **not been built or run**. Build order:
  1. `dotnet build` - the `RCallArgument` signature change, the new
     `IRChannelListener` constructor parameter, the `RErrorException`
     constructor signature change (now 3 args, was 1), and the new
     `AwesomeAssertions` package reference are all recent enough that
     a clean build isn't a given; check the test project resolves the
     new package first if restore is slow/fails.
  2. `RValueCodecTests` and `FrameCodecTests`/`RConnectionTests` (pure
     C#, no R) - as before, run
     `Double_NaReal_And_ComputedNaN_StayDistinct_AfterRoundTrip` first,
     then the new `Table_*` tests.
  3. `r/tests/testthat.R` (`Rscript tests/testthat.R` from the `r/`
     directory, or `testthat::test_dir("tests/testthat")`) - pure R,
     no C# process, and cheaper to debug than the integration tests if
     something in `write_r_value`/`read_r_value`/the registry
     functions is wrong. This is genuinely untested against a real R
     interpreter as of this writing, including the guard added to
     worker.R's final block (`getOption("rwire.testing")`) - if
     `source()`-ing worker.R for tests doesn't behave as expected,
     start here.
  4. `EvalCallIntegrationTests`, `HandleLifecycleTests`,
     `TablePerformanceTests` (need `Rscript`, share
     `RWireProcessFixture`) - `HandleLifecycleTests` depends on
     `EvalAsync` working (it uses `EvalAsync("exists(...)")` as a
     diagnostic probe into the R-side registry), so fix
     `EvalCallIntegrationTests` first if both are failing.
  5. `ProcessSupervisorTests` (needs `Rscript`, own isolated processes,
     not the shared fixture) - this is where the three bug fixes above
     should actually be re-verified against real output, since they
     were fixed from a description of a failure, not by re-running
     the fixed code.
- **The `EnsureReady` fix (allowing `Busy` through) has not been
  re-tested.** It's a small, logically clear change, but the original
  failure came from a real concurrency race - re-run
  `HandleLifecycleTests.Dispose_ReleasesTheHandle_FromTheRSideRegistry`
  specifically (ideally a few times, given it's timing-sensitive) 
  before trusting the fix.
- `worker.R`'s registry functions (`rwire_registry_*`) still haven't
  been checked against a real R interpreter directly - `testthat`
  now gives a cheap way to do that (`test-registry.R`) without needing
  the full C# integration path; use it first if something here seems
  wrong.
- The `HandleLifecycleTests` tests that poll for the background
  `ReleaseHandleBestEffort` `Task.Run` to complete
  (`Dispose_ReleasesTheHandle_FromTheRSideRegistry`,
  `TwoHandlesViaCreateRef_BothMustBeDisposed_BeforeObjectIsFreed`) have
  a real timing dependency - if these are flaky on a slow machine,
  that's a test-tuning issue, not necessarily a correctness bug; check
  the non-timing assertions in the same test first.
- `worker.R`'s frame, RValue, Table, and registry/handle-ID wire shape
  are all still hand-kept in sync with the C# side across several
  files (`MsgType.cs`/`RTypeTag.cs`/`RValueCodec.cs`/`RValue.cs`) and
  this doc's own description of each. This surface has grown
  significantly across Phases 2-4 with no shared source of truth
  between the two languages - still worth codegen if a future phase
  has room for it.
- `TablePerformanceTests`' logged timings are exactly that - logged,
  not asserted against a threshold. Don't read a passing test run as
  a performance claim; read the numbers in the test output once you
  have a real machine to run them on, and remember Phase 4's
  known-buffered (not streamed) implementation means today's numbers
  aren't representative of what the design is ultimately meant to
  achieve.

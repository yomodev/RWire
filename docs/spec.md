# RWire — Specification & Implementation Plan

## 1. Overview

A .NET client library that manages the full lifecycle of an `RScript.exe`
worker process and communicates with it over a custom binary protocol.
The R side is pure R script (no compiled C extensions to author or
maintain) using base R plus accepted package dependencies
(`data.table`; no Arrow/Flight — see §6.4).

**Targets**: R ≥ 4.4, .NET 10. Performance is a first-class design
constraint throughout: `Span<T>`/`Memory<T>`, `MemoryMarshal.Cast` for
zero-copy reinterpretation, `ArrayPool<T>`, and endian-matched wire
encoding so casts never require element-by-element conversion.

### 1.1 Goals

- Deterministic process lifetime management (start, health-check,
  crash-recover, graceful shutdown).
- A compact, versioned, length-prefixed binary protocol.
- Correct mapping of R's vector/NA/attribute semantics to .NET types.
- Reference-counted handles so C# can hold long-lived references to
  objects that live in R's memory without copying them across on every
  call.
- A protocol and codec implementation that is **channel-agnostic**:
  the same frame/codec logic runs over a socket today and over a named
  pipe or other transport later, without change.
- Both **synchronous and asynchronous** call APIs on the C# side, as
  genuine independent execution paths (not one faked on top of the
  other).
- High-throughput transfer of very large tabular data (order of
  10M rows × 1000 columns, or collections of such tables) in both
  directions, via a first-class table wire type — see §6.

### 1.2 Non-goals (v1)

- Multiple concurrent in-flight requests per R worker (single
  request/response at a time per connection; concurrency is achieved
  by running multiple R worker processes, not by pipelining one).
- Full fidelity for arbitrary S4 classes, closures, or environments as
  transferable values (only as opaque server-side handles).
- Apache Arrow / Arrow Flight as a dependency or wire format —
  evaluated and deliberately rejected; rationale in §6.4.

---

## 2. Architecture

```
+---------------------+       IRChannel (socket today)      +--------------------+
|   C# Client          |  <----- binary protocol ------->    |   R worker process |
|                       |                                     |  (RScript.exe)     |
|  - ProcessSupervisor  |         stdout/stderr pipe          |  - message loop    |
|  - RConnection        |  <----- (logging/crash only) ---    |  - object registry |
|  - FrameCodec         |                                     |  - dispatcher      |
|  - HandleRegistry     |                                     |                    |
+---------------------+                                      +--------------------+
```

Two independent channels:

1. **Data channel** — carries the framed binary protocol, over
   whichever `IRChannel` implementation is configured (socket in v1).
2. **Diagnostic channel** — redirected stdout/stderr, used only for
   logging and as one of the crash-detection signals. Never carries
   protocol data.

### 2.1 Channel abstraction

The frame codec (header parsing, dispatch, vector/table encode-decode)
is written once as pure functions over `Span<byte>` /
`ReadOnlySpan<byte>`, with zero dependency on the transport. Each
concrete channel implements a dual sync/async interface natively —
neither direction is derived from the other, to avoid deadlock risk
(sync-over-async) or wasted overhead (async-over-sync):

```csharp
interface IRChannel
{
    int Read(Span<byte> buffer);
    void Write(ReadOnlySpan<byte> buffer);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);
}
```

- **Socket** (v1): `NetworkStream.Read`/`ReadAsync` map directly —
  both are real, independent I/O paths.
- **Named pipe** (future): `PipeStream` has the same dual API.
- **Memory-mapped file** (future, if ever needed for same-host
  extreme-scale transfer): no real syscall to await — the "async"
  member is a thin wrapper over the same synchronous memory copy, and
  that's not a compromise, there's genuinely no I/O to overlap.
  Requires the R-side `mmap` package (no built-in MMF support in base
  R) — acceptable now that package dependencies are allowed, but not
  needed for v1 given the decision in §6.3.

Two thin execution loops (one calling `Read`/`Write`, one calling the
`Async` pair) drive the same shared codec — adding a channel later
means writing a new `IRChannel`, not touching the protocol.

---

## 3. Process Lifecycle Management

### 3.1 Launch

C# starts `RScript.exe` with:

```
RScript.exe worker.R --channel=socket --port=<port> --token=<session-token>
```

- `--channel` — reserved for future channel types (socket is the only
  v1 implementation).
- `--port` — C# pre-binds a loopback `TcpListener` on an ephemeral
  port and passes it in; R connects out to C#, rather than C# trying
  to discover a port R chose. Avoids a race, needs no handshake
  file/registry.
- `--token` — random per-session value the R side must echo in its
  first `HELLO` frame, so a stray/previous process can't attach to a
  new listener.

### 3.2 State machine

```
NotStarted → Starting → Ready → Busy ⇄ Ready → Faulted → Restarting → Ready
                                            ↘ Disposed (from any state)
```

| State | Meaning |
|---|---|
| `Starting` | Process launched, waiting for `HELLO` + token match |
| `Ready` | Idle, channel connected, last heartbeat healthy |
| `Busy` | Request in flight |
| `Faulted` | Process exited unexpectedly, heartbeat timed out, or protocol error (bad frame, unexpected type) |
| `Restarting` | Kill (if still alive) → clean up channel/registry → relaunch → re-`HELLO` |
| `Disposed` | Terminal; graceful shutdown sent, resources released |

### 3.3 Health monitoring

- `Process.Exited` event → immediate `Faulted`.
- Heartbeat: C# sends `PING` on an idle timer (e.g. every 5s) when in
  `Ready`; no `PONG` within timeout → `Faulted`. Catches a hung R
  script that hasn't crashed.
- stdout/stderr lines are logged and scanned for fatal signatures as a
  secondary signal only, never the sole trigger (R can legitimately
  print "Error in" from a caught, non-fatal condition).

### 3.4 Restart policy

Exponential backoff (e.g. 200ms, 400ms, 800ms, capped, with a max
retry count before surfacing a permanent failure). All outstanding
handles in the registry are invalidated on restart — callers holding a
handle across a restart get an error on next use, by design (no
cross-process handle recovery in v1).

### 3.5 Shutdown (Dispose)

1. If `Ready`/`Busy`: send `SHUTDOWN` frame, await graceful process
   exit up to a grace period.
2. If still running after grace period: `Process.Kill(entireProcessTree: true)`.
3. Close channel, dispose listener, clear handle registry.

---

## 4. Wire Protocol — Control Plane

### 4.1 Frame format

All multi-byte integers are little-endian.

```
+------------+------------+------------------+------------+----------------+
| Length(4)  | MsgType(1) | CorrelationId(4) | PayloadLen | Payload(N)     |
+------------+------------+------------------+------------+----------------+
```

- `Length` — total frame length after this field.
- `MsgType` — one byte, see §4.2.
- `CorrelationId` — client-assigned request ID; response echoes it. `0`
  reserved for server-initiated frames (none in v1 besides `HELLO`).

### 4.2 Message types

| Value | Name | Direction | Purpose |
|---|---|---|---|
| 0x01 | `HELLO` | R → C# | Sent once on connect; token + R version |
| 0x02 | `PING` | C# → R | Heartbeat request |
| 0x03 | `PONG` | R → C# | Heartbeat response |
| 0x04 | `EXEC` | C# → R | Evaluate a script/expression, no return value expected |
| 0x05 | `EVAL` | C# → R | Evaluate an arbitrary expression, return the value — flexible escape hatch |
| 0x06 | `CALL` | C# → R | Invoke a named function with typed/handle arguments — the fast structured path (see §4.4) |
| 0x07 | `GET_OBJ` | C# → R | Fetch value referenced by handle |
| 0x08 | `SET_OBJ` | C# → R | Store a value, get back a new handle |
| 0x09 | `CREATE_REF` | C# → R | Increment refcount on existing handle |
| 0x0A | `RELEASE_REF` | C# → R | Decrement refcount / free |
| 0x0B | `SHUTDOWN` | C# → R | Graceful stop |
| 0x0C | `RESULT` | R → C# | Successful response (atomic value or table, per §5/§6) |
| 0x0D | `ERROR` | R → C# | Failure response (R condition message + call), connection stays alive |

### 4.3 Payload encoding — control messages

Simple TLV-style encoding for scalars/strings (handle IDs, tokens,
function/expression text): `[TypeTag(1)][Len(4)][Bytes]`. Kept
independent from the vector/table encoding so control-plane parsing
never has to go through the data decoder.

### 4.4 `CALL` — why it exists alongside `EVAL`

`EVAL` (string → `parse()` → `eval()`) covers everything but has three
costs in a tight loop: parse overhead on every call, string-building
arguments is injection-fragile once they come from arbitrary data, and
it can't cleanly take a *handle* as an argument (would require
smuggling it through a variable name).

`CALL` payload: `[FunctionName][ArgCount]{[ArgIsHandle(1)][Arg]}*`,
where each argument is tagged as either an inline value (decoded with
the normal vector/table encoder) or a handle reference (resolved via
lookup in the registry environment). R side does
`do.call(functionName, argsList)` — base R, no parsing step, and a
handle can be passed straight into a function call without its
underlying data ever crossing the wire.

---

## 5. Type Mapping — Atomic Vectors

R has no scalars — only vectors — and every atomic type carries its
own NA sentinel baked into the bit pattern, which the wire format
preserves for free.

| R type | Wire encoding | NA detection | C# type |
|---|---|---|---|
| `NULL` | type tag only, no body | n/a | `null` |
| `logical` | see §5.2 | sentinel value | `bool?[]` |
| `integer` | 4 bytes/element LE | `== int.MinValue` (`INT_MIN`) | `int?[]` |
| `double` | 8 bytes/element LE | low 32 bits of the 64-bit pattern `== 1954` (R's `NA_real_` payload) — **not** the same test as "is NaN"; a genuine `NaN` (e.g. from `0/0`) has a different payload and must stay `double.NaN`, not collapse to `null` | `double[]` + validity handled at a higher layer (see §5.1) |
| `character` | length-prefixed UTF-8 strings; length `-1` = NA | length sentinel | `string?[]` |
| `raw` | raw bytes, no NA concept | n/a | `byte[]` |
| `list` | count + per-element `[TypeTag][Value]` | n/a | `object?[]` (tagged) |

### 5.1 Where nullability conversion happens

NA/NaN bit-level detection is a **low-level decoding concern only**.
The low-level decoder exposes the raw bit-accurate representation;
converting to idiomatic nullable C# types (`double?`, `int?`, etc.)
happens at a **higher API layer**, not inside the wire codec itself.
This keeps the codec fast and allocation-light (no boxing into
`Nullable<T>` per element during the hot decode loop) while still
giving calling code a comfortable nullable-typed surface if it wants
one, via a separate conversion step applied on demand.

### 5.2 Logical encoding — negotiated, not fixed

R stores `logical` internally as a 4-byte int (0 / 1 / `INT_MIN`=NA) —
identical sentinel to `integer`. Two wire representations are
supported:

- **Wide** (4 bytes/element): zero R-side transform cost — `writeBin`
  writes the underlying bytes verbatim.
- **Compact** (1 byte/element, `0/1/2=NA`): needs a vectorized remap
  (`ifelse(is.na(x), 2L, as.integer(x))`) before `writeBin(..., size=1)` —
  cheap (single vectorized pass), but not free.

Chosen per-message based on vector size (compact only pays off for
large logical vectors) rather than fixed at the protocol level.

### 5.3 Attributes

R attributes (`attributes(x)`) are themselves a named list, and an
attribute's value can be any R object — encoding is naturally
recursive using the same value-encoder already defined.

- **Fast-path attributes** — `names`, `dim`, `class` get dedicated
  header slots rather than going through generic recursion:
  ```
  [TypeTag(1)][ElementCount(4)][HasNames(1)][Elements...][Names?]
  ```
- **Generic attribute block** (optional, trailing) — anything else
  (`levels` on a factor, arbitrary `attr<-`):
  ```
  [AttrCount(4)] { [NameLen+Name][Value, recursively encoded] }*
  ```
- **Factors** get `class = ["factor"]` via the fast-path Class slot;
  `levels` rides the generic attribute block as a single recursive
  entry rather than getting its own dedicated header slot. This is a
  narrower "fast path" than originally described here — implementation
  found that fast-pathing `class` alone (avoiding the slow, fully
  generic attribute-list path for the one attribute that's genuinely
  hot) already captures the practical benefit, and a fully bespoke
  factor wire shape wasn't worth the added complexity. See
  `docs/progress.md`'s "Decisions changed since spec.md".
- C# side surface: an `RValue` wrapper exposing `Names`/`Dim`/`Class`
  as first-class optional properties plus a generic `Attributes`
  dictionary catch-all, so consumers can pattern-match on `Class`
  (e.g. detect a factor) without needing real S4 support.

---

## 6. Large Tabular Data — `TABLE` Wire Type

### 6.1 Why tables get their own type

A data.frame/data.table is **not** a generic nested object needing
`serialize()` — it's a list of homogeneous, equal-length column
vectors. Each column is exactly the atomic-vector shape §5 already
encodes. Routing it through the atomic-vector encoder column-by-column
avoids `serialize()` entirely for the common, large, performance-
critical case.

### 6.2 Wire shape

```
[TABLE][RowCount(4)][ColCount(4)]
  { [ColNameLen+Name][ColTypeTag] }*                 -- schema, once
  { <column payload, §5 atomic vector encoding> }*   -- data, column by column
```

- **Schema once, not per chunk** — the one Arrow-Flight idea worth
  keeping structurally, and it's free to take without taking Arrow.
- **No pre-buffered whole-table blob.** Row/column count is known
  before any column is written, and each column's exact byte length
  is `length(x) * elementSize` — computable up front. R writes each
  column with `writeBin(x, socketConnection, size = ..., endian =
  "little")` **directly onto the open connection**, not into an
  intermediate raw vector first: one copy (R's vector memory → OS
  socket buffer), not a serialize-into-buffer-then-send double copy.
  A single `double` column of 10M rows is 80MB — a perfectly
  reasonable one-shot buffer; no intra-column chunking or batching is
  needed. The "1000 columns" figure is a *loop count* over that cheap
  per-column operation, not a peak-memory concern, since only one
  column is ever in flight at a time.
- On the C# read side, the equivalent move is reading each
  length-known column payload directly into a caller-supplied
  `Span<double>`/`Memory<double>` (sized from the header) rather than
  a scratch buffer plus copy — `PipeReader`'s `ReadOnlySequence<byte>`
  supports this without an extra hop. Row/column counts from the
  header let C# **pre-allocate exact-size destination arrays** before
  any column data arrives.
- **Nulls/dictionaries handled by R's native representation, not a
  separate mechanism**: doubles/integers/logicals already carry NA in
  the bit pattern (no extra validity-bitmap bytes needed), and a
  factor already *is* codes + levels, so no separate "dictionary
  encoding" concept is required — §5.3's factor fast path covers it.
- `class` (so C# can tell `data.frame` from `data.table` from a plain
  named list) travels via the attribute block from §5.3.

### 6.3 A list of tables

`[LIST_OF_TABLES][Count]` followed by N sequential `TABLE` frames on
the same connection, no outer buffering. C# exposes this as
`IAsyncEnumerable<RTable>` (async path) or `IEnumerable<RTable>` (sync
path), so table *n* can be consumed while table *n+1* is still
arriving — essential rather than optional at the scale under
discussion (50 tables of tens of GB each is not something to hold
fully in memory on either side at once).

### 6.4 Why not Apache Arrow / Arrow Flight

Evaluated and explicitly rejected, for concrete reasons rather than
unfamiliarity:

- **No throughput advantage for this use case.** Arrow's format is
  contiguous columnar buffers written to a stream — mechanically the
  same `memcpy` + socket `send()` path as the custom encoding above.
  Arrow's SIMD-optimized code paths help *computation over* the data,
  not the R→socket→C# transfer step, which is bound by memory
  bandwidth/syscall throughput regardless of wire format.
  10M rows × 1000 columns is a *cumulative transfer time* concern
  (bounded by physics either way), not a peak-memory concern, because
  R has already fully materialized the object before transfer starts
  regardless of format — Arrow's incremental-batching machinery solves
  a producer-side memory problem this system doesn't have.
- **The two genuinely useful Flight ideas are cheap to take without
  Arrow**: schema-once (§6.2) and handle-based retrieval (already
  present via the handle registry + `GET_OBJ`/`CALL`, matching
  Flight's ticket-based `DoGet`).
- **R's native NA representation and factor structure already give
  Arrow's other two ideas "for free"** — no explicit validity bitmap
  or dictionary-batch concept needed (§6.2).
- **Structural mismatch for hosting**: R's `arrow` package is a Flight
  *client*, not a mature Flight *server* — hosting a Flight service
  from R would mean dropping to C++/Python for that side, breaking the
  "plain R script, fully controlled" architecture. Flight also has no
  concept of process lifecycle, restart-on-crash, or object handles —
  §3's `ProcessSupervisor` and the handle registry would still need to
  be built from scratch on top of it, for no data-plane win.
- **Disk/mmap spill was considered and also rejected** for the same
  reason external files were ruled out generally: slower media,
  read/write overhead not worth it given the transfer volume is a
  realistic, expected part of normal operation rather than an edge
  case — the socket-streamed columnar `TABLE` format is the primary
  and only planned mechanism for bulk transfer.
- Revisit Arrow-the-library only if a future requirement is genuinely
  about *external* interop (e.g., handing a result to Python/Polars/
  DuckDB without going through the C# layer) — not for R↔C# throughput.

### 6.5 Feeding R from C# (reverse direction)

Symmetric, and simpler since C# arrays are already contiguous:

- C# sends `[TABLE][RowCount][ColCount]{schema}{columns}`, writing
  each column directly from its backing array via
  `stream.Write(MemoryMarshal.AsBytes(span))` — no copy, no boxing.
- R reads knowing the row count up front, so it pre-allocates the
  exact-size vector per column via `readBin(con, what = "double", n =
  n)` (allocates the right size in one call — no grow-and-copy
  antipattern).
- Assemble columns into `list(...)` then set `class`, or use
  `data.table::setDT()` — sets the class in place without duplicating
  the list, which matters at this scale given `data.table` is the
  accepted R-side dependency.

---

## 7. Serialization Strategy (Non-Tabular Cold Path)

For objects that aren't rectangular/homogeneous (irregular lists,
mixed lengths, nested arbitrary objects, S3/S4 objects with no
tabular shape): R's built-in `serialize()`/`unserialize()`, wrapped in
the frame format as an opaque blob (`TypeTag = SERIALIZED_BLOB`). No
custom C code either way — `serialize` is a base-R function. Since its
output size isn't knowable up front, this is the one path where a
single measure-then-send buffering cost is accepted (via
`rawConnection()`) — acceptable because it's the less performance-
critical branch by construction; anything large and regular belongs
in §6's `TABLE` type instead, not here.

Every payload starts with a `TypeTag` so the decoder on both sides can
dispatch without ambiguity, and the hot (§5/§6) vs. cold (this
section) path choice is invisible to the protocol consumer.

---

## 8. Reference Counting / Handle Management

- R-side **object registry**: an `environment()` used as a hashtable,
  keyed by a 64-bit handle ID. **R allocates** the ID (monotonic
  counter) and returns it on `SET_OBJ`, avoiding collision handling on
  the R side.
- Refcount lives **only on the R side**, incremented on `CREATE_REF`,
  decremented on `RELEASE_REF`, entry removed at zero.
- C# side: `RHandle : SafeHandle` (or a thin `IDisposable` wrapping
  one) — `Dispose()` sends `RELEASE_REF`; a finalizer exists as a
  last-resort net but is not the primary release path (GC timing isn't
  deterministic enough to rely on).
- **Crash/restart safety**: the registry is session-scoped. On
  reconnect after a restart, C# does not replay old handles — they're
  invalid by construction (new process, empty registry). Pending
  `RHandle` instances from the old session throw on next use.
- **Leak guard**: a periodic sweep on the R side removing entries
  older than N heartbeats with an untouched refcount, as a backstop
  against a C# client that hard-crashes without disposing
  (belt-and-suspenders on top of session-scoping).

---

## 9. Performance

- Shared codec written once against `Span<byte>`/`ReadOnlySpan<byte>`
  (§2.1) — no per-channel duplication of encode/decode logic.
- `System.IO.Pipelines` (`PipeReader`/`PipeWriter`) for frame parsing
  with backpressure and `ReadOnlySequence<byte>` slicing (no
  intermediate copies for header parsing), used by the async path.
- `ArrayPool<byte>.Shared` for payload buffers above a size threshold;
  `stackalloc` for small fixed headers.
- Sync path: blocking I/O on a dedicated thread per R worker
  connection — justified by the strict request/response pattern on a
  single long-lived local connection (avoids thread-pool/continuation
  overhead). Validate against the async path with the benchmark in
  §11.6 rather than assuming.
- Endianness matched end-to-end (little-endian, R's `writeBin`/
  `readBin` with explicit `endian = "little"`) specifically so
  `MemoryMarshal.Cast<byte, double>` / `<byte, int>` reinterpretation
  works directly on both atomic vectors and table columns — no
  per-element conversion loop, no byte-swap cost.
- Nullability conversion (§5.1) deliberately kept out of the hot
  decode loop — raw bit-accurate values first, nullable-type
  conversion as an optional higher-level step.

---

## 10. Open Design Points to Revisit

1. Named-pipe channel as a same-host alternative if a firewall/AV
   product interferes with loopback sockets — slots into `IRChannel`
   without protocol changes (§2.1).
2. Memory-mapped-file channel for extreme same-host throughput beyond
   what socket streaming achieves — same data model, transport-only
   change, requires the R `mmap` package. Not needed for v1 given
   §6.4/§6.2's socket-streamed `TABLE` design; revisit only if
   profiling shows the socket copy itself is the bottleneck.
3. Multi-worker pooling (round-robin over N R processes) — out of
   scope for v1 but `ProcessSupervisor` should not preclude it.

---

## 11. Implementation Plan

### Phase 0 — Skeleton & handshake
- `RScript.exe` process launch with argument passing.
- Loopback `TcpListener` pre-bind + pass port to R.
- R `worker.R`: parse args, connect, send `HELLO` with token.
- C#: `RConnection` validates `HELLO`, transitions `Starting → Ready`.
- **Exit criteria**: process launches, handshake completes, clean
  dispose shuts it down.

### Phase 1 — Channel abstraction & frame protocol
- `IRChannel` interface (§2.1) with a socket implementation.
- Frame reader/writer (length-prefix, `MsgType`, `CorrelationId`) as
  channel-agnostic codec, both sync and async execution loops.
- `PING`/`PONG`, `SHUTDOWN`, `ERROR`.
- Heartbeat timer + timeout → `Faulted` transition.
- **Exit criteria**: heartbeat keeps connection alive over both sync
  and async call paths; killing the R process externally is detected
  within one heartbeat interval.

### Phase 2 — Atomic type mapping (hot path)
- Vector header encode/decode for `logical` (both encodings, §5.2),
  `integer`, `double`, `character`, `raw`, with NA sentinel handling
  both directions (§5, incl. the NaN-vs-NA bit distinction).
- Attribute block (`names`/`dim`/`class` fast path + generic block,
  §5.3), factor fast path.
- `EVAL` / `CALL` / `GET_OBJ` / `SET_OBJ` for atomic vectors.
- `MemoryMarshal.Cast` fast path for `double`/`int`; nullable
  conversion implemented as a separate higher-level step (§5.1).
- **Exit criteria**: round-trip a vector of each atomic type,
  including NA, zero-length, and attributed (named/factor) vectors,
  byte-identical after round-trip.

### Phase 3 — Reference counting
- R-side object registry (environment-as-hashtable), `CREATE_REF`/
  `RELEASE_REF`.
- C# `RHandle : SafeHandle`.
- Leak-guard sweep on R side.
- **Exit criteria**: handle lifecycle test suite (§12.4) passes,
  including the crash/no-dispose scenario.

### Phase 4 — `TABLE` type & bulk transfer
- Schema + column-by-column streaming encode/decode (§6.2), both
  directions (§6.5 for C#→R).
- `LIST_OF_TABLES` streaming (§6.3), exposed as
  `IAsyncEnumerable<RTable>`/`IEnumerable<RTable>`.
- data.table-aware assembly (`setDT`) on the R ingest side.
- **Exit criteria**: a multi-million-row, multi-column table round-
  trips correctly and within the throughput target set after
  benchmarking (§12.6); a list of several such tables streams without
  requiring the full set in memory on either side at once.

### Phase 5 — Cold path (serialize/unserialize) + irregular lists
- `SERIALIZED_BLOB` type tag, wraps R's native `serialize()`.
- Irregular `list` encoding using recursive `[TypeTag][Value]`.
- **Exit criteria**: nested/irregular list and a non-tabular S3 object
  round-trip correctly.

### Phase 6 — Process supervision & resilience
- Full state machine, exponential backoff restart, stdout/stderr
  capture and log correlation with the failure event.
- Chaos tests (§12.5).
- **Exit criteria**: killing the R process mid-request surfaces a
  clean exception to the caller and the supervisor recovers
  automatically for the next call.

### Phase 7 — Performance hardening
- `ArrayPool` integration throughout, sync-vs-async benchmark (§12.6)
  used to confirm or revise §9's blocking-I/O choice.
- **Exit criteria**: throughput target for large numeric/table
  transfer (define target once Phase 4 baseline is measured).

---

## 12. Testing Strategy

### 12.1 Unit tests — protocol layer (no R process involved)
- Frame encode/decode round-trip for every `MsgType`.
- Truncated/corrupt frame handling (short read, bad length, unknown
  `MsgType`) → well-defined exception, not a hang or crash.
- Vector encode/decode for each atomic type: empty vector, single
  element, large vector, all-NA vector, mixed NA vector.
- Explicit test distinguishing `NA_real_` from a computed `NaN`
  (e.g. `0/0`) surviving round-trip as different values.
- `character` encoding with UTF-8 multi-byte content and embedded NA.
- Attribute block round-trip: named vector, factor (codes + levels),
  a `dim`-bearing vector.
- Endianness assumptions asserted explicitly (test fails loudly if
  ever run on a big-endian target).

### 12.2 Unit tests — R side (pure R, no socket)
- `writeBin`/`readBin` round-trip for each type at the R level, byte
  layout asserted against a fixed expected hex sequence (catches
  accidental drift if R's internal representation assumptions
  change).
- Object registry: create/release/refcount increment-decrement,
  double-release behavior (decide and test), sweep-on-expiry logic.
- Table schema/column writer: byte length of a column matches
  `length(x) * elementSize` computed independently, for each atomic
  type.

### 12.3 Integration tests — real R process, real channel
- Full round-trip: launch → handshake → `EVAL`/`CALL` of a known
  expression → assert result, over both sync and async C# call paths.
- All atomic types end-to-end through the real channel.
- `GET_OBJ`/`SET_OBJ`/`CREATE_REF`/`RELEASE_REF` full lifecycle
  against a live R registry.
- `TABLE` round-trip through the real channel, R→C# and C#→R.
- Graceful `SHUTDOWN` — process exits within grace period, exit code
  checked.

### 12.4 Handle lifecycle tests
- Dispose releases handle (assert R-side registry no longer contains
  it, via a diagnostic query).
- Handle used after Dispose throws.
- Two `RHandle`s to the same underlying object via `CREATE_REF`; both
  disposed independently; object freed only after the second Dispose.
- Simulated crash (kill R process without sending `RELEASE_REF`):
  confirm supervisor restarts cleanly and old handles fail fast
  rather than silently pointing at nothing.
- A `CALL` passing a handle as an argument resolves correctly without
  the underlying data crossing the wire (assert via message size).

### 12.5 Chaos / resilience tests
- Kill `RScript.exe` mid-request → caller gets an exception, not a
  hang; supervisor transitions to `Restarting` and recovers.
- R script throws inside `EVAL`/`CALL` (caught R condition) → `ERROR`
  frame with message, connection stays alive (non-fatal path, no
  restart).
- Heartbeat timeout simulated by pausing the R process → `Faulted`
  detected within timeout window, restart triggered.
- Kill the R process mid-`TABLE` transfer (partway through a large
  column stream) → clean exception on the C# side, no partial/corrupt
  table silently accepted.
- Repeated rapid restart (induced crash loop) respects the backoff
  policy and eventually surfaces a permanent-failure error rather than
  looping forever.

### 12.6 Performance tests / benchmarks
- Throughput (MB/s) for large `double[]` vector transfer and for a
  large `TABLE` transfer (multi-million rows × many columns), hot path
  vs. a naive `serialize()`-based baseline, to justify the hybrid
  approach.
- Latency of a minimal round-trip (`PING`/`PONG`) to quantify fixed
  per-call overhead.
- Sync-blocking vs. async/`Pipelines` comparison under the expected
  single-connection request/response pattern — decides §9's open
  question empirically.
- Large vector/table with NA interleaved — confirm NA handling doesn't
  regress the fast path.
- `MemoryMarshal.Cast` fast path vs. an element-by-element conversion
  baseline, to quantify the zero-copy win concretely.

### 12.7 Test tooling notes
- R-side unit tests: given package dependencies are now acceptable,
  `testthat` is a reasonable choice rather than a hand-rolled
  assertion script — confirm before Phase 0 tooling setup.
- C# side: standard xUnit/NUnit; integration, chaos, and large-table
  performance tests tagged separately from unit tests so CI runs the
  fast unit suite on every change and the slower suites less
  frequently.

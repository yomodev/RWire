# Real TABLE Streaming — Design

Twice-deferred (Phase 4 → Phase 7 → here — see `docs/spec-deviations.md`
§6.2 for the full history). This is the design Phase 7 said was needed
before attempting it a third time: not an estimate, but a concrete plan
covering exactly what changes, why, and how it can be verified without
a compiler in this sandbox.

**Status: designed, not implemented.** Everything below is a proposal
for whoever next has a build available. No source files are touched by
this document.

## 1. Restating the actual problem

Both `RValueCodec.Encode` (C#) and `write_r_value`/`build_result_payload`
(R) fully materialize an encoded value into an in-memory buffer
(`ArrayBufferWriter<byte>` / a `rawConnection`) before anything is sent.
The frame format then writes `[LengthPrefix][Header{payloadLength}]`
*before* the payload — so by the time either side calls `Send`/
`write_frame`, the payload already has to exist in full to know its own
length. For ordinary `EVAL`/`CALL` payloads this is fine (small,
milliseconds). For a `TABLE` with a 10M-row `double` column, this means
an 80MB buffer sitting in memory before a single byte reaches the
socket — spec.md §6.2's explicit goal ("no pre-buffered whole-table
blob... one copy, not a serialize-into-buffer-then-send double copy")
is not met.

Phase 7 identified the mechanism correctly and stopped at a real fork:
give `IBufferWriter<byte>` a flush hook (doesn't exist on the
interface — only `PipeWriter` has one), or make `Encode` `async` so it
can `await` a flush between columns (breaks every call site's
signature). Both are real costs. **Neither is actually necessary.**

## 2. The resolution: two independent, low-risk pieces

### 2a. A streaming `IBufferWriter<byte>` needs no flush hook

`IBufferWriter<byte>.Advance(count)` doesn't have to mean "append to a
growing array." An implementation is free to write the bytes out
immediately when `Advance` is called, keeping only a small bounded
scratch buffer:

```csharp
internal sealed class StreamingChannelWriter : IBufferWriter<byte>
{
    private readonly IRChannel _channel;
    private readonly byte[] _scratch;   // e.g. 64 KB, fixed
    private int _pending;               // bytes written into _scratch, not yet flushed

    public StreamingChannelWriter(IRChannel channel, int scratchSize = 64 * 1024)
    {
        _channel = channel;
        _scratch = new byte[scratchSize];
    }

    public void Advance(int count) => _pending += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureRoom(sizeHint);
        return _scratch.AsMemory(_pending);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureRoom(sizeHint);
        return _scratch.AsSpan(_pending);
    }

    private void EnsureRoom(int sizeHint)
    {
        sizeHint = Math.Max(sizeHint, 1);
        if (_pending + sizeHint <= _scratch.Length)
        {
            return;
        }

        Flush(); // makes room at the start of _scratch

        if (sizeHint > _scratch.Length)
        {
            // Rare (e.g. one very long string): fall back to an
            // exact-size transient buffer for this one call instead of
            // growing the shared scratch buffer permanently.
            throw new NotSupportedException(
                "Caller must chunk writes larger than the scratch buffer; " +
                "see 'Oversized single writes' below.");
        }
    }

    public void Flush()
    {
        if (_pending > 0)
        {
            _channel.Write(_scratch.AsSpan(0, _pending));
            _pending = 0;
        }
    }
}
```

(Sketch, not final code — error handling, the async twin using
`WriteAsync`, and the oversized-write case below are omitted for
brevity.)

Everything `RValueCodec.Encode` does — `WriteByte`/`WriteInt32`/
`WriteDouble` (small, bounded calls) and `writer.Write(byte[])` /
`writer.Write(ReadOnlySpan<byte>)` (the `BuffersExtensions.Write`
extension method, used for `Logical`/`Raw` columns and for character
UTF-8 bytes) — already goes through `GetSpan`/`Advance` in bounded
chunks; `BuffersExtensions.Write` in particular loops internally,
requesting more space as needed rather than one giant span for the
whole array. **This means `RValueCodec.Encode` itself does not need to
change at all.** Passing it a `StreamingChannelWriter` instead of an
`ArrayBufferWriter<byte>` is enough to make it stream — same proven,
already-tested logic, different destination.

**Oversized single writes**: `WriteNullableString` for one very long
string, or a raw column written via one `writer.Write(bigSpan)` call
where a chunk offered by `BuffersExtensions.Write`'s internal loop
could still exceed 64KB in a single `GetSpan` request — needs a real
answer, not the placeholder exception above. Two options: (a) size the
scratch buffer to the largest single value the wire format can produce
in one `GetSpan` call before `write_r_value`'s own chunking kicks in —
not zero-risk, since a single mega-string field is exactly the kind of
value RWire doesn't otherwise limit; or (b) make `EnsureRoom` fall back
to a one-off `ArrayPool<byte>.Shared.Rent(sizeHint)` for a request
larger than the scratch buffer, write straight to the channel from
that rented buffer, and return it — bounded per-call, not
proportional to the whole table. **(b) is the recommended answer** —
it keeps peak memory bounded by the single largest field rather than
by table size, without an arbitrary hard limit that could reject a
legitimately large string column value.

### 2b. The length-prefix problem: a cheap dry-run pass, not a duplicated formula

The frame header needs the total payload length *before* the streaming
write begins. The tempting shortcut — write a second, parallel
`ComputeEncodedLength(RValue value)` function that mirrors `Encode`'s
structure but only sums sizes — is a real drift risk: `Encode` and
`ComputeEncodedLength` would have to be kept in exact lockstep by hand
forever, and a future change to one that's forgotten in the other
produces a wrong length prefix, which corrupts the frame in a way
nothing catches until the receiver's `ReadExact` either hangs or reads
garbage.

**Better: reuse `Encode` unchanged for the length calculation too**, by
giving it a counting `IBufferWriter<byte>` that discards bytes and just
tracks how many would have been written:

```csharp
internal sealed class CountingBufferWriter : IBufferWriter<byte>
{
    private readonly byte[] _scratch = new byte[4096]; // reused, contents ignored
    public long Length { get; private set; }

    public void Advance(int count) => Length += count;
    public Memory<byte> GetMemory(int sizeHint = 0) => _scratch.AsMemory(0, Math.Max(sizeHint, 1));
    public Span<byte> GetSpan(int sizeHint = 0) => _scratch.AsSpan(0, Math.Max(sizeHint, 1));
}
```

(This needs the same "sizeHint bigger than scratch" handling as 2a —
same fix: return a fresh array sized to `sizeHint` on that rare path,
nothing is retained either way.)

Sending a table then becomes: run `Encode` once against a
`CountingBufferWriter` to get the exact length, send the frame header
with that length, then run `Encode` **again** against a
`StreamingChannelWriter` to actually transmit. `Encode` runs twice, but
the second pass's cost is dominated by cache-friendly sequential array
reads and socket I/O, not allocation — encoding a `double[10_000_000]`
twice is cheap CPU work compared to either the memory pressure of
buffering 80MB or the I/O time of sending it. **Zero duplicated
encode/decode logic, zero drift risk** — both passes are the exact same
already-tested method.

### 2c. New `RConnection` surface

```csharp
public void SendTable(MsgType msgType, uint correlationId, RValue table);
public ValueTask SendTableAsync(MsgType msgType, uint correlationId, RValue table, CancellationToken ct = default);
```

Internally: `CountingBufferWriter` pass → `EncodeHeaderOnly` with the
counted length → `StreamingChannelWriter` pass (`Flush()` at the end to
push any remainder). `ProcessSupervisor.SetObj`/`SetObjAsync` call this
instead of the current `ArrayBufferWriter` + `Send`/`SendAsync` path
**only when `value.TypeTag == RTypeTag.Table`** — every other call
(`Eval`, `Call`, small `SetObj` values, everything else) keeps using
the existing, proven, unchanged path. This is a deliberately narrow
change: the new code only activates for the one case it exists to fix.

## 3. R side (`worker.R`) — the symmetric problem

`build_result_payload`/`write_frame` have the identical shape: build
into a `rawConnection`, then `write_frame` uses `length(payload)` for
the header. The fix is symmetric in spirit but R's dynamic typing means
there's no equivalent of "run the same function twice with a different
sink" as cleanly — `write_r_value` writes directly via `writeBin(x,
con)`, and a `rawConnection` opened in write mode doesn't expose a
"count without storing" mode.

Two workable approaches for R, in order of preference:

1. **A real dry-run "counting" connection.** R doesn't have a built-in
   no-op connection, but `write_r_value` never inspects what it wrote
   afterward — it's pure output. A tiny custom connection (R's
   `connections` extension mechanism, or more simply a closure-based
   fake object matching just the subset of `writeBin`/`close`
   `write_r_value` actually calls) could accept and discard bytes,
   returning only a running total. This mirrors `CountingBufferWriter`
   exactly and has the same zero-drift-risk property: `write_r_value`
   itself is untouched, only the connection it's handed differs.
   Needs verifying that R's connection interface can actually be
   extended this cheaply from a plain R script (no C code allowed, per
   spec.md's "no C code on the R side" constraint) — this is the one
   open feasibility question in this whole design, and the reason this
   is a design doc rather than a diff: it should be checked against
   real R documentation/experimentation before committing to it.
2. **A direct arithmetic formula**, computed once for `TABLE` values
   only (not needed for the general case, since only `TABLE` is
   performance-critical): for each column, `1 (type tag) + 4 (element
   count) + <data bytes, computed directly from the vector: length*8`
   for double, `length*4` for integer, `length*1` for logical/compact,
   `sum(nchar(x, type="bytes")) + 4*length` for character (`nchar(...,
   type="bytes")` is itself a cheap vectorized single pass, not a
   per-string R-level loop) `+ <attribute block bytes, same
   arithmetic>`. This avoids needing a fake connection at all, at the
   cost of `write_r_value`'s column-encoding logic and this length
   formula needing to be kept in sync by hand — the exact drift risk
   §2b was designed to avoid on the C# side. Given R's side of the
   `TABLE` format only has a handful of cases (5 atomic types, no
   nested tables-within-tables), this is a much smaller surface to
   keep in sync than the general `Encode` method would be, which makes
   the risk more tolerable here than it would be in C# — but it is a
   real, ongoing cost, not a free lunch. **Recommended if approach 1
   turns out not to be cheaply buildable in plain R** — safer to ship
   a small hand-verified formula for 5 known cases than to leave TABLE
   streaming undone.

Once the length is known, R writes the header via the existing
`write_frame`'s header-writing logic (factored out, or duplicated
inline for a table-specific `write_table_frame`) and then calls
`write_r_value(socket_connection, value)` **directly against the real
socket connection** instead of a `rawConnection` — this half requires
no new abstraction at all, since `writeBin` already writes wherever
`con` points; the only change is which connection object gets passed
in, once its total length is known up front.

## 4. Read side — harder, and not fully designed here

The write side (§2–3) is symmetric and low-risk once the length-prefix
problem is solved. The **read** side is structurally more invasive:

- `RConnection.Receive()`/`ReceiveAsync()` currently read the *entire*
  payload into one `ArrayPool<byte>.Shared.Rent(payloadLength)` buffer
  before `RValueCodec.Decode` ever runs — `Decode` is offset-based over
  a fully-present `ReadOnlySpan<byte>`, not a streaming reader.
- **A real streaming decode needs to read the schema (small: row/col
  count + column names/types) first, then read each column's bytes
  directly into a pre-sized destination array** (`double[]`, `int[]`,
  etc.) — matching spec.md §6.2's original description — rather than
  through an intermediate full-payload byte buffer.
- This is a bigger structural change than the write side: it means a
  new decode path that owns its own reads directly against
  `IRChannel`/`RConnection`, separate from the existing `Frame`-based
  `Receive`/`ReceiveAsync` used by everything else, plus a symmetric
  change to `read_r_value` on the R side (currently reads the whole
  frame payload into a `rawConnection` first — see `read_frame`/
  `payload_con <- rawConnection(frame$payload, "r")` at the call sites
  in `worker.R`).
- **Concrete, independent motivation for doing this beyond the
  original streaming goal**: `ArrayPool<byte>.Shared` does not pool
  arbitrarily large arrays — requests above the pool's bucket ceiling
  fall back to an ordinary (unpooled) allocation, and an allocation
  that size lands on the Large Object Heap. A large `TABLE` payload
  today is very likely to hit exactly this case on every single
  receive, meaning the "pooled" buffer in `Receive`/`ReceiveAsync` is
  probably not actually being pooled at all for the sizes that matter
  most. **This should be verified with a real .NET runtime and the
  exact pool configuration in use** rather than asserted from memory
  here — but if confirmed, it's a second, independent reason (beyond
  the double-buffering §1 already covers) that large-table receive
  is more expensive than it looks, and worth citing in `phase-8-plan.md`
  as added weight for prioritizing this work once §2–3 are done.

**Recommendation: implement and verify §2–3 (write side) first, fully,
before starting the read side.** The write side is lower-risk, fully
designed here, and delivers real value on its own (`SetObj`/
`SetObjAsync` for large tables, C#→R). The read side needs its own
dedicated design pass — ideally after the write side's
`StreamingChannelWriter`/`CountingBufferWriter` pattern has been
built and verified once, since a lot of the same "bounded scratch
buffer, don't touch the proven codec" thinking will transfer directly
to designing its read-side counterpart.

## 5. Verification plan (for whoever has a build)

1. Unit-test `CountingBufferWriter` and `StreamingChannelWriter` in
   isolation first — feed each a variety of `RValueCodecTests`-style
   values (including the oversized-single-write case from §2a) against
   a fake `IRChannel`/in-memory sink, independent of the rest of the
   system.
2. Cross-check: for the same `RValue`, assert
   `CountingBufferWriter.Length` equals
   `ArrayBufferWriter<byte>.WrittenCount` after the existing `Encode`
   call — i.e., prove the counting pass agrees with the existing,
   trusted buffered path before trusting it for a real length prefix.
3. Add a large-table round-trip test (mirroring
   `RConnectionTests`' existing large-payload tests) that sends a
   multi-million-row table via the new `SendTable`/`SendTableAsync` and
   confirms the receiver (still the existing whole-buffer `Decode` at
   this stage, since §4 is separately scoped) reconstructs it
   correctly.
4. Only after 1–3 pass: attempt the R-side counting-connection
   feasibility question from §3, with real R available to experiment
   against rather than guessing from documentation.
5. Benchmark before/after with `TablePerformanceTests` (once that
   harness has actually been run at all — see `phase-8-plan.md` Tier 3)
   to confirm this delivers a measurable peak-memory reduction, not
   just a theoretical one.

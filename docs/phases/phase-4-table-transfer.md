# Phase 4 — `TABLE` Type & Bulk Transfer

Status: not started. See `../progress.md` for overall project status.

## Goal

The performance-critical centerpiece: move data.frame/data.table-
shaped data (potentially 10M+ rows × 1000+ columns) in both
directions without ever buffering a whole table, and stream a list of
tables without holding the full collection in memory on either side.

## Prerequisites

Phase 2 complete (atomic vector encode/decode, since each column reuses
it directly). Phase 3 not strictly required but useful (`GET_OBJ`/
`CALL` returning a handle to a large table you then stream via
`TABLE` is a natural combination to test together).

## Reference

`../spec.md` §6 in full — this phase has the most design detail
already locked in; read all of §6 before starting, not just this doc.

## Checklist

### Schema + column streaming — R → C#
- [ ] `TABLE` frame header: `RowCount`, `ColCount`, then the schema
      block (`[ColNameLen+Name][ColTypeTag]` per column) — written
      once, not repeated.
- [ ] Per-column write: compute byte length from `length(x) *
      elementSize` **before** writing anything (no measure-by-
      buffering for this path — that's the cold path's approach, not
      this one).
- [ ] `writeBin(column, socketConnection, size = ..., endian =
      "little")` **directly onto the open connection** — verify via a
      test or code review that no intermediate raw-vector buffer of
      the whole column is being built first.
- [ ] Reuse Phase 2's atomic encoders as-is for each column's payload
      — this phase should not need new per-type encoding logic, only
      the table-level framing around it.
- [ ] `class` attribute (data.frame vs. data.table vs. plain named
      list) travels via Phase 2's attribute block.
- [ ] Factor columns use Phase 2's factor fast path — no separate
      "dictionary encoding" concept (spec §6.2 is explicit that this
      is intentional, not a gap).

### C# receive side
- [ ] Pre-allocate exact-size destination arrays per column
      immediately after reading `RowCount`/`ColCount`/schema — before
      any column data arrives.
- [ ] Read each column's payload directly into the pre-allocated
      destination (`Span<double>`/`Memory<double>` sized from the
      header) — no scratch-buffer-then-copy.
- [ ] If using `PipeReader`: use `ReadOnlySequence<byte>` slicing
      directly into the destination rather than an extra copy hop.
- [ ] Expose the result as a typed columnar container — not
      `object[][]` (boxed) — e.g. a `Dictionary<string, Array>` or a
      small purpose-built `RTable` type with typed column accessors.

### Reverse direction — C# → R
- [ ] C# writes `[TABLE][RowCount][ColCount]{schema}{columns}`,
      writing each column via `stream.Write(MemoryMarshal.AsBytes(span))`
      directly from the backing array (no copy, no boxing).
- [ ] R reads `RowCount` first and pre-allocates each column via
      `readBin(con, what = "double", n = n)` (or the appropriate
      `what=` per type) — one-shot allocation, not grow-and-append.
- [ ] Assemble into `list(...)` then set `class`, or use
      `data.table::setDT()` for in-place class assignment without
      duplicating the list.

### List of tables
- [ ] `LIST_OF_TABLES` frame: `[Count]` followed by `Count` sequential
      `TABLE` frames on the same connection.
- [ ] C# async surface: `IAsyncEnumerable<RTable>` that yields each
      table as its frame completes, without waiting for the full set.
- [ ] C# sync surface: `IEnumerable<RTable>` with the same
      per-table-as-it-arrives behavior (a blocking iterator, not one
      that reads everything up front and then yields).
- [ ] Test with enough tables/rows that "read everything into memory
      first" would be clearly wrong (doesn't need to be the full
      10M×1000×50 scenario — a scaled-down version that would still
      fail an accidental full-buffering implementation is enough for
      CI; save true full-scale runs for the performance benchmarks in
      Phase 7).

## Exit criteria (from spec.md §11 Phase 4)

- [ ] A multi-million-row, multi-column table round-trips correctly.
- [ ] Throughput is within the target set after Phase 7 benchmarking
      (target isn't fixed yet — see Phase 7 — but this phase's
      implementation should not have obvious inefficiencies like
      double-buffering that would need revisiting later).
- [ ] A list of several tables streams without requiring the full set
      in memory on either side at once (verify this concretely, e.g.
      via a memory-usage assertion or by using tables large enough
      that "all in memory at once" would fail on the test machine).

## Notes for resuming mid-phase

This phase is where "reuse Phase 2, don't reinvent it" matters most —
if resuming and something about column encoding looks different from
Phase 2's atomic vector format, that's a red flag to check against
spec.md §6.2 rather than assume it was an intentional divergence.

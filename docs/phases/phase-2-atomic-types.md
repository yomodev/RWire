# Phase 2 — Atomic Type Mapping (Hot Path)

Status: not started. See `../progress.md` for overall project status.

## Goal

Move real R values across the wire: the atomic vector types, their NA
sentinels, and attributes (names/dim/class, factors). Add `EVAL` and
`CALL` for atomic-vector-returning expressions/functions, and
`GET_OBJ`/`SET_OBJ` (handles themselves arrive properly in Phase 3,
but these two messages need at least a stub registry to test against).

## Prerequisites

Phase 1 complete: frame codec working, heartbeat/shutdown solid.

## Reference

`../spec.md` §5 (type mapping, NA detection, logical encoding,
attributes), §4.4 (`CALL`), §4.2 (message types).

## Checklist

### Vector header + atomic encodings
- [ ] Vector header struct: `TypeTag`, `ElementCount`, `HasNames` per
      spec §5.3's header layout.
- [ ] `double`: 8 bytes/element LE. Implement NA detection exactly as
      spec §5 describes — `BitConverter.DoubleToInt64Bits`, mask low
      32 bits, compare to `1954`. **Write the NaN-vs-NA distinction
      test first** (a computed `NaN` like `0.0/0.0` must round-trip as
      `double.NaN`, not collapse to a null/NA marker) — this is the
      easiest part of the spec to get subtly wrong.
- [ ] `integer`: 4 bytes/element LE, NA = `int.MinValue`.
- [ ] `logical`: implement **both** wide (4 bytes) and compact
      (1 byte, `0/1/2=NA`) encodings per spec §5.2; decide the
      size-threshold heuristic for which one a given message uses and
      write it down in code comments (spec deliberately leaves the
      exact threshold as an implementation choice).
- [ ] `character`: length-prefixed UTF-8 strings, length `-1` = NA
      sentinel, distinct from an empty string (length `0`).
- [ ] `raw`: straight bytes, no NA handling needed.
- [ ] R-side: `writeBin`/`readBin` with `endian = "little"` explicitly
      set on every call (don't rely on a default) for every type
      above — this is what makes `MemoryMarshal.Cast` valid on the C#
      side.

### C# decode surface — nullability kept out of the hot loop
- [ ] Low-level decoder returns raw values (e.g. `double[]` with the
      bit-pattern intact, `int[]` with `int.MinValue` sentinels still
      in place) — no `Nullable<T>` boxing in this step (spec §5.1).
- [ ] Separate, optional conversion step/extension method
      (`ToNullableArray()` or similar) that a caller can apply to get
      `double?[]`/`int?[]`/`bool?[]` — exercised by tests, not forced
      on every decode.
- [ ] `MemoryMarshal.Cast<byte, double>` / `<byte, int>` used for the
      raw decode of numeric vectors — no manual per-element loop for
      the common case.

### Attributes
- [ ] `names` — fast-path slot in the vector header (already
      scaffolded above); encode/decode a named vector correctly,
      including partial names (R allows a mix of named/unnamed
      elements via `""` for unnamed).
- [ ] `dim` — fast-path slot; a matrix round-trips with its dimensions
      intact.
- [ ] `class` — fast-path slot.
- [ ] Generic attribute block (spec §5.3) for anything else, encoded
      recursively using the same value encoder.
- [ ] Factor fast path: integer codes + `class="factor"` + `levels`
      character vector, without falling through the generic attribute
      block.
- [ ] C# `RValue` wrapper type: `Names`, `Dim`, `Class` as first-class
      optional properties, `Attributes` dictionary catch-all.

### `EVAL` and `CALL`
- [ ] `EVAL`: payload is an expression string; R does
      `eval(parse(text = ...))`, wraps in `tryCatch`, returns a
      `RESULT` frame (atomic value) or `ERROR` frame.
- [ ] `CALL`: payload is `[FunctionName][ArgCount]{[ArgIsHandle][Arg]}*`
      per spec §4.4; R does `do.call(functionName, argsList)`. For
      this phase, args are atomic values only — handle-typed args need
      Phase 3's registry, so stub that branch to throw "not yet
      supported" rather than silently misbehaving.
- [ ] `ERROR` frame carries the R condition message and call; verify
      the connection **stays alive** after a caught R error (this is
      the non-fatal path — don't let an `ERROR` response trigger any
      reconnect/restart logic, that's reserved for Phase 6's chaos
      scenarios).

## Exit criteria (from spec.md §11 Phase 2)

- [ ] Round-trip a vector of each atomic type — including NA values
      and zero-length vectors — byte-identical after round-trip.
- [ ] Named vector and factor round-trip with attributes intact.
- [ ] `NA_real_` vs. computed `NaN` distinguished correctly after
      round-trip (explicit test, not just implied by the NA test).
- [ ] `EVAL` of a simple expression and `CALL` of a simple function
      both return correct atomic results.
- [ ] A deliberately erroring `EVAL`/`CALL` returns `ERROR` without
      killing the connection.

## Notes for resuming mid-phase

If resuming here, run the NaN-vs-NA test first, before touching
anything else — it's the single most likely thing to have been done
subtly wrong, and everything downstream (tables in Phase 4 reuse this
exact same double-encoding logic) inherits the bug if it's wrong here.

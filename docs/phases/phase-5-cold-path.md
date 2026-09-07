# Phase 5 — Cold Path (serialize/unserialize) + Irregular Objects

Status: implemented, unverified (no build/test run yet — see
`../progress.md`).

## Goal

Handle R values that don't fit any of the fast-path shapes (atomic
vectors, factors, lists, tables) — S4 objects, environments, closures,
language objects — without erroring, by falling back to R's own
`serialize()`/`unserialize()`. C# treats the result as an opaque byte
blob it can round-trip but not inspect.

## Reference

`../spec.md` §7 (Serialization Strategy / Cold Path).

## What's implemented

- `RTypeTag.SerializedBlob` (value `8`).
- `RValue.SerializedBytes` + `RValue.OfSerializedBlob(byte[])`.
- `RValueCodec`: both `Encode` and `Decode` have an early return for
  this tag — **no attribute block** follows it on the wire (unlike
  every other type), since `serialize()`/`unserialize()` already embed
  the value's own attributes/class internally. Wire shape is just
  `[TypeTag(1)][Length(4)][Bytes]`.
- `worker.R`: `write_r_value`'s final `else` branch (previously
  `stop("unsupported type")`) now calls `serialize(x, connection =
  NULL)` and returns early, skipping `write_attributes()`.
  `read_r_value` has a matching early-return branch calling
  `unserialize()` on the received bytes, skipping `read_attributes()`.
- This composes automatically with existing List/Table handling — a
  list containing one closure among ordinary vectors encodes the
  closure via the cold path and everything else normally, since
  `write_r_value` is called per-element already.

## What this does NOT do

- C# cannot deserialize R's `serialize()` format — `SerializedBytes`
  is meant to be round-tripped unexamined (received from one R call,
  passed back via `SET_OBJ`/`CALL` to another), not inspected client-
  side. This is a deliberate scope boundary, not a gap: writing a
  compatible R-serialize-format parser in C# would be a substantial
  project of its own and isn't needed for the "shuttle an opaque R
  object between calls" use case this exists for.
- No special-casing for *which* R types hit this path — it's whatever
  falls through every other branch (S4, environments, closures,
  language objects, and anything else not explicitly handled). A
  custom S3 class built on top of an atomic vector (e.g. `difftime`)
  does **not** need this path — it's already handled by the existing
  atomic-vector + generic-attribute mechanism from Phase 2, since its
  underlying storage is already one of the mapped types.

## Checklist

- [ ] `dotnet build` succeeds with the `SerializedBlob` additions.
- [ ] `RValueCodecTests`' four new `SerializedBlob_*` tests pass
      (round-trip, empty bytes, no-attribute-block byte-count check,
      encode-without-bytes-set throws).
- [ ] `r/tests/testthat/test-value-codec.R`'s four new cold-path tests
      pass (environment round-trip, closure round-trip that still
      *works* after unserializing, list-containing-a-closure encodes
      only that element via the cold path, wire-shape self-consistency
      check).
- [ ] `ColdPathIntegrationTests.cs` (needs `Rscript`) passes — in
      particular `SerializedBlob_RoundTripsThroughR_ViaSetObjAndCall`,
      which is the real end-to-end proof: a closure R can't represent
      any other way survives `EVAL → SET_OBJ → CALL` and is still a
      genuinely callable function on the other side, not just
      byte-identical.

## Notes for resuming mid-phase

If something in this phase misbehaves, check in this order: (1) the
pure C# codec tests first (fastest signal, no R involved), (2) the
`testthat` tests (pure R, no C# process — will tell you if
`write_r_value`/`read_r_value`'s early returns are wired correctly
independent of the socket), (3) the full integration tests last. The
`do.call`-calling-`do.call` indirection in
`SerializedBlob_RoundTripsThroughR_ViaSetObjAndCall` is intentional,
not a mistake — `handle_call` in `worker.R` always resolves its first
argument as a function *name* (a string), so invoking a handle-held
closure requires calling `do.call` itself as the named function, with
the closure and its arguments as `do.call`'s own two arguments.

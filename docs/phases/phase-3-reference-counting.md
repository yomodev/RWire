# Phase 3 — Reference Counting

Status: not started. See `../progress.md` for overall project status.

## Goal

Let C# hold long-lived references to R-side objects via opaque
handles, with correct refcounting, leak protection, and clean
behavior across a crash/restart.

## Prerequisites

Phase 2 complete: atomic values flow correctly (handles will carry
atomic values as their payload for `SET_OBJ`).

## Reference

`../spec.md` §8 (reference counting / handle management).

## Checklist

### R-side registry
- [ ] An `environment()` used as a hashtable, keyed by handle ID
      (string keys work fine in an R environment; convert the 64-bit
      ID to a string key internally).
- [ ] Monotonic counter for ID allocation, **R allocates, not C#**
      (spec is explicit about this — avoids collision handling on the
      R side).
- [ ] Registry entry stores: the value, a refcount, and a
      last-touched timestamp (for the leak-guard sweep).
- [ ] `SET_OBJ`: store value, allocate ID, refcount starts at 1,
      return the ID.
- [ ] `GET_OBJ`: look up by ID, return the value using the existing
      atomic/table encoders — this message doesn't touch refcount.
- [ ] `CREATE_REF`: increment refcount for an existing ID.
- [ ] `RELEASE_REF`: decrement; remove entry when it reaches zero.
      Decide and document the double-release behavior (no-op vs.
      explicit error) — spec flags this as an open choice, pick one
      now.
- [ ] Leak-guard sweep: periodic (e.g. tied to the heartbeat tick)
      pass removing entries whose last-touched timestamp is older than
      N heartbeats — this is a backstop, not the primary release path.

### C# side
- [ ] `RHandle : SafeHandle` (or a thin `IDisposable` wrapping one —
      pick one, `SafeHandle` is the more idiomatic .NET choice for
      "unmanaged-ish resource with a finalizer safety net").
- [ ] `Dispose()` sends `RELEASE_REF`; finalizer exists as a last
      resort but tests should never rely on finalization firing
      (force it explicitly via `GC.Collect()` +
      `GC.WaitForPendingFinalizers()` only in a test specifically
      about the finalizer path, not as the normal test pattern).
- [ ] Handle used after Dispose throws a clear exception
      (`ObjectDisposedException` or a custom equivalent).
- [ ] On `ProcessSupervisor` restart (informal in this phase, real
      state machine in Phase 6): all outstanding `RHandle` instances
      from the old session must fail on next use — implement this via
      a session ID stamped on each handle, checked before sending any
      message.

### `CALL` with handle arguments
- [ ] Revisit Phase 2's stub: `CALL` arguments tagged as handles now
      resolve via registry lookup on the R side instead of throwing.
- [ ] Test: pass a handle into a `CALL` and assert (via message byte
      size, not just correctness of the result) that the underlying
      data never crossed the wire — this is the actual point of
      having handles at all.

## Exit criteria (from spec.md §11 Phase 3)

- [ ] Full handle lifecycle test suite passes (mirrors spec §12.4):
  - [ ] Dispose releases handle (verified via a diagnostic query
        against the R registry).
  - [ ] Handle used after Dispose throws.
  - [ ] Two handles to the same object via `CREATE_REF`; object freed
        only after both are disposed.
  - [ ] Simulated crash (kill R process without `RELEASE_REF`):
        supervisor recovers, old handles fail fast rather than
        pointing at nothing silently.

## Notes for resuming mid-phase

The double-release decision (no-op vs. error) and the handle-wrapper
choice (`SafeHandle` vs. plain `IDisposable`) are the two things most
likely to have been decided ad hoc if this phase was started and
paused — check `progress.md`'s "Decisions changed since spec.md"
section first for either, since spec.md deliberately left them open.

# Phase 8 — Feedback-Driven Hardening: Forward Plan

This is the concrete plan referenced from `docs/progress.md`'s
"Current phase" section. It exists because progress.md tracks status,
not sequencing — this file says what order the remaining Phase 8 work
happens in and why, so the next session doesn't have to re-derive
priority from a flat bullet list.

Do not re-litigate items already resolved in progress.md's "Decisions
changed since spec.md" section (e.g. PING/PONG split, handle ID width,
double-release semantics) — those are closed. This plan only covers
what's still open.

Read `docs/progress.md` first for full context on what's already done
this session. Nothing below duplicates that.

---

## Ordering principle

Two things earn priority over everything else on the pending list:

1. **Verify before extending.** Phase 7 has never been built or run
   (see progress.md "Notes / blockers"). Every Phase 8 change this
   session sits on top of that unverified ground. The first thing
   whoever has a real .NET/R environment should do — before touching
   anything in this plan — is the four-step verification progress.md
   already lays out (`dotnet build`, the two new large-payload
   `RConnectionTests`, the full existing suite, then the benchmark
   tests). This plan assumes that happens first; it is not itself a
   step below because it requires a machine this sandbox doesn't have.
2. **Close explicitly-flagged gaps before starting new design work.**
   This plan document itself was the first such gap. `IProcessSupervisor`
   + README docs (item 2 in progress.md's pending list) is next because
   it's small, self-contained, and was asked for directly — unlike the
   larger design questions below, it doesn't need a diff-from-plan
   writeup as a prerequisite.

With that, the remaining pending items group into three tiers:

### Tier 1 — small, self-contained, do next

- **`IProcessSupervisor` + README usage docs.** Done — see
  progress.md.
- **Consolidated "what diverged from spec.md and why" document.**
  Done — `docs/spec-deviations.md`. (Originally scoped as Tier 2, but
  turned out not to need the `ProcessSupervisor` decomposition or
  TABLE streaming design as prerequisites after all — it only needed
  progress.md's existing "Decisions changed" content reorganized by
  topic, plus one real fix found in the process: spec.md §5.2 still
  described a negotiated wide/compact logical encoding that was never
  built, inconsistent with §4.2/§5.3 which had already been corrected
  to match implementation. Corrected as part of this pass.)
- **Track down the specific flaky test.** Blocked on having NLog
  output from actual test runs (added this session) — can't be done
  from this sandbox. Whoever runs the Phase 7 verification pass should
  watch for it and report back which test and what the log shows.
- **Project-wide `dotnet format` cleanup pass.** Cosmetic, low-risk,
  mechanical. Do it any time there's a spare cycle with a compiler
  available — no reason to sequence it precisely, just don't let it
  block anything else.

### Tier 2 — needed the deviations doc for context (now available)

- **`ProcessSupervisor` responsibility decomposition.** Done, partially
  — see `docs/phases/processsupervisor-decomposition.md`. Two
  self-contained pieces (the static wire-codec helpers, the
  stdout/stderr diagnostics ring buffer) were extracted into
  `ProcessSupervisorWireCodec` and `DiagnosticsBuffer` — mechanical,
  zero behavior change, no dependency on the class's core
  process/connection state. The larger lifecycle/dispatch/supervision
  split is explicitly **not** attempted yet: tracing the actual field
  usage found that `_connectionLock` does not guard `_connection`
  during a restart — `State`/`_restartGate` are what keep that window
  safe from a concurrent caller instead — which means splitting
  lifecycle from dispatch is a real synchronization redesign, not a
  mechanical move, and doing it blind (no compiler/test runner in this
  sandbox) risks introducing exactly the kind of race that's hardest
  to catch after the fact. See the decomposition doc's
  "Recommendation" section for what to answer first once a real
  environment is available.
- **Real TABLE streaming design.** Done —
  `docs/phases/table-streaming-design.md`. Turns out neither fork Phase
  7 identified (`IBufferWriter` flush-hook, `async Encode`) is
  necessary: a streaming `IBufferWriter` implementation doesn't need a
  flush hook if it writes through in small bounded chunks instead of
  growing an array, and the length-prefix problem is solved by running
  the *existing, unchanged* `Encode` twice — once against a byte-
  counting sink, once against the streaming one — rather than
  maintaining a second hand-written length formula that could drift
  out of sync. Covers the write side (C#→R) in full implementable
  detail, including a new `RConnection.SendTable`/`SendTableAsync` and
  where `ProcessSupervisor.SetObj`/`SetObjAsync` would call it (only
  for `RTypeTag.Table` values — every other path is untouched). Flags
  the read side (R→C#, and large `GetObj` results) as needing its own
  follow-up design pass — it's structurally more invasive since
  `RValueCodec.Decode` is offset-based over an already-fully-present
  span — and notes a second, independent motivation for eventually
  doing it: large-table receives likely aren't actually served from
  `ArrayPool.Shared`'s pooled buckets today (worth confirming on a
  real runtime), meaning the "pooled" rent in `RConnection.Receive` is
  probably an unpooled Large-Object-Heap allocation for the sizes that
  matter most. One open feasibility question left for whoever
  implements the R side: whether a cheap dry-run "counting connection"
  is buildable in plain R (no C code) the way `CountingBufferWriter`
  is in C#, or whether R needs the fallback hand-written-formula
  approach instead — see the design doc §3.

### Tier 3 — needs a real machine, can be designed but not finished here

- **`System.IO.Pipelines`-based rewrite of `RConnection`'s read/write
  path.** Can be designed on paper, but validating it actually
  improves anything needs the benchmark harness running on real
  hardware. Sequence this after the TABLE streaming design above,
  since both touch `RConnection`'s I/O path and doing them as one
  coordinated change is lower-risk than two separate rewrites of the
  same file.
- **Benchmark harness execution and results.** `SyncVsAsyncBenchmarkTests`
  and `TablePerformanceTests` already exist (Phase 7) but have never
  run. Needs to happen before trusting any performance claim in this
  project, including the "should we rewrite the R side in C" question,
  which remains a prose estimate.
- **Cross-platform (Linux) verification.** Write the Dockerfile/WSL
  setup whenever convenient (no blocking dependency), but the actual
  verification run needs a machine.
- **OS-level zombie-process mitigation** (Windows Job Objects /
  `prctl(PR_SET_PDEATHSIG)`). Needs P/Invoke code that can't be
  meaningfully written-and-trusted without a compiler to check it
  against each platform's actual API surface. Lower urgency than the
  above — the `ProcessExit`-handler mitigation already in place covers
  the ordinary-shutdown case, and the hard-kill case this would close
  is inherently a best-effort improvement, not a correctness fix.

---

## IProcessSupervisor design notes

(Kept here rather than only in code comments, since the constraint is
worth stating plainly before implementation: `RHandle` calls back into
its owner via `internal void ReleaseHandleBestEffort(ulong, long)` on
the *concrete* `ProcessSupervisor` — see `RHandle.cs`. Extracting
`IProcessSupervisor` does not change this. The interface exists so
host application code that depends on "something that can Eval/Call/
SetObj/GetObj/CreateRef against an R session" can mock that dependency
in its own tests — it is not a general DI abstraction over
`ProcessSupervisor`'s internals, and `RHandle` remains tied to the
concrete class regardless of which type a caller holds it through.
This is a deliberate scope boundary, not an oversight: making
`RHandle` fully interface-agnostic would mean either exposing
`ReleaseHandleBestEffort` publicly (undesirable — it's not part of the
supported call surface) or having the interface carry it internally
too (which defeats the purpose of a clean mockable surface). If a
future consumer genuinely needs to mock handle release as well, that's
a sign that boundary needs revisiting then, with a concrete use case
in hand, rather than solved speculatively now.)

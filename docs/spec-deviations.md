# RWire — Deviations from spec.md

`docs/spec.md` is the locked-in design. Implementation across Phases
1–8 has diverged from it in a number of places — some because spec.md
documented shorthand that wasn't a workable wire value, some because a
planned property turned out not to be justified without benchmark
data, and some because a goal (real TABLE streaming) is still open.

This document consolidates every such divergence in one place,
organized by the spec section it affects, so a reader doesn't have to
reconstruct it from session-by-session narrative in
`docs/progress.md`. **spec.md itself has been corrected in-place**
wherever a divergence is now the actual design (see each entry below)
— this document exists for the ones that are corrections-with-history
worth keeping visible, and for the ones still open rather than
resolved.

If you're deciding whether something here still applies: entries
marked **(resolved, spec.md corrected)** are settled — spec.md already
reflects them and there's nothing left to do. Entries marked **(open)**
are still pending; see `docs/phases/phase-8-plan.md` for where they
sit in the forward plan.

---

## §4.2 — Message types

**PING/PONG split into distinct MsgType codes** (`PING = 0x02`,
`PONG = 0x03`, shifting every later value in the table up by one).
The original spec table's shared `0x02` for both was documentation
shorthand for "the heartbeat message type," not a workable wire value
for two frames that travel in opposite directions and need to be
told apart on receipt. **(resolved, spec.md corrected)** —
`MsgType.cs` is the source of truth for the actual numbering; spec.md
§4.2's table matches it.

## §5.2 — Logical encoding

Spec described a negotiated wide/compact pair of encodings for
logical vectors. **Implemented: only the compact (1-byte) wire
encoding.** There's no benchmarking data yet showing the wide encoding
would earn its added complexity. **(resolved, spec.md corrected —
open design question remains)** — §5.2 now describes compact-only as
the actual design (it previously still described the negotiated pair,
inconsistently with implementation; fixed alongside this document).
The underlying question of whether compact's per-element remap cost
ever matters is still **(open)** — revisit if profiling (once the
Phase 7 benchmark harness actually runs — see `phase-8-plan.md`) shows
it's measurable at realistic table sizes.

## §5.3 — Attributes

Spec's description of factor encoding was broader than what got
built. **Implemented: only `class` is fast-pathed for factors;
`levels` rides the generic attribute block as one recursive entry**
rather than getting a dedicated wire slot. **(resolved, spec.md
corrected)** — §5.3 now describes this as the actual design; there was
no measured need for a dedicated `levels` slot once the generic
attribute path existed anyway.

## §6.2 — TABLE wire shape / zero-copy goal

The wire *format* for `TABLE` is faithful to spec. The performance
property that motivated designing `TABLE` as a first-class type at
all — streaming/zero-copy transfer instead of buffering the whole
encoded value — **is not implemented.** Both sides still buffer the
entire encoded value in memory before sending.

This has been deferred twice: Phase 4 (initial `TABLE` implementation)
and Phase 7 (performance hardening) both left it open. Phase 7 made a
real, narrower fix — `RConnection.Send`/`SendAsync` no longer copy the
payload an extra time on the way out — but `RValueCodec.Encode` itself
remains one-shot-into-one-writer. Making it flush per-column needs
either an `IBufferWriter` flush-hook (doesn't exist on the current
interface) or an `async Encode` signature (a breaking change to every
existing call site). That was judged too risky to attempt without a
compiler available to verify it, especially right after Phases 1–5
had just been confirmed working through actual manual testing.

**(open)** — this is the single largest deviation from spec's stated
goal for `TABLE` and the most consequential to resolve. See
`phase-8-plan.md`'s Tier 2 for the design work needed before
attempting it a third time, and Tier 3 for how it relates to a
possible `System.IO.Pipelines` rewrite of the same I/O path.

## §6.4 — Apache Arrow / Arrow Flight

Not a deviation — recorded here because it's a decision that could
look reconsidered given how much §6.2's goal remains unmet. It hasn't
been. **Custom protocol for both control and data plane remains the
design**; Arrow/Arrow Flight was evaluated and rejected for the
reasons in spec.md §6.4, and the fact that streaming isn't built yet
doesn't change that reasoning — it's a gap in *this* protocol's
implementation, not evidence the protocol choice was wrong.

## §8 — Reference counting / handle management

Several implementation-level decisions here weren't fully specified
in spec.md's prose and got settled during Phase 3/6 implementation.
All **(resolved, spec.md corrected or consistent with spec's intent)**:

- **`RHandle` is a plain `IDisposable` class with a finalizer, not
  `SafeHandle`.** `SafeHandle` is shaped around native/unmanaged
  handles with OS-level semantics; RWire's handle is a logical 64-bit
  ID with no OS resource behind it, so `SafeHandle`'s machinery
  doesn't fit.
- **Double-release is a no-op, not an error.** A client-side double
  release (`Dispose()` racing a finalizer, or a caller mistake calling
  `Dispose()` twice) is normal and harmless. Erroring on it would make
  defensive `Dispose()` patterns actively dangerous — exactly the
  pattern client code is supposed to use.
- **Handle IDs are allocated as 32-bit R integers**, not the full
  64-bit range the wire format's 8-byte slot implies. Base R has no
  native 64-bit integer without the `bit64` package, and ~2 billion
  objects/session is far beyond any realistic need. The wire slot
  stays 8 bytes (high word always zero) so the format doesn't need to
  change if the allocator ever does.
- **A disposed-handle mistake is validated and thrown *before*
  acquiring the connection lock / entering `Busy` state.** Using an
  already-disposed `RHandle` is a client programming error, not a
  connection failure, and must not fault the supervisor by being
  routed through the same path as a real protocol error.

## §2.1 — Channel abstraction

**`ProcessSupervisor` depends on `IRChannelListener`/
`Func<IRChannelListener>`, never a concrete `TcpListener`,** for
establishing the channel. This extends Phase 1's `IRChannel`
abstraction (already in spec) to the connection-*establishment* side
too, and — as of Phase 6 — supports restart by minting a fresh
listener per attempt, since a listener generally can't be reused once
its connection has been accepted and disposed. **(resolved, spec.md
consistent with this)** — a natural extension of an abstraction spec
already called for, not a divergence from intent.

A related, smaller change from Phase 8: **`IRChannelListener.Port`
(an `int`) became `IRChannelListener.ChannelArgument` (a `string`)**
so a named-pipe or remote-TCP implementation doesn't have to invent a
fake port number to satisfy the interface. `TcpRChannelListener`
implements it as the port number stringified;
`ProcessSupervisor.Port` becomes a computed convenience (parses
`ChannelArgument` as an int, `-1` if it doesn't parse) so existing
TCP-based code didn't need to change. The R worker's CLI argument was
renamed `--port=` → `--endpoint=` to match (`worker.R` updated
correspondingly). **(resolved)** — this generalizes spec's
channel-agnostic goal rather than departing from it.

## §3.3 — Health monitoring

**Fatal-signature scanning of stdout/stderr was not implemented as an
independent restart trigger.** Spec requires (§3.3) that any such
signal stay "a secondary signal, never the sole trigger" — a
regex-based pattern matcher feeding a decision already made reliably
by process-exit/heartbeat-timeout signals wasn't worth the
false-positive risk of matching R's freeform error text. **(resolved,
by design)** — the recent-diagnostics ring buffer
(`RecentDiagnosticOutput`, added Phase 6) achieves the actual goal
(correlating output with a fault after the fact) without that risk.
Nothing further planned here.

## §9 / §12.6 — Performance, benchmarking

Not a design deviation but a verification gap worth surfacing here
since it touches several of the above: **the benchmark scaffolding
Phase 7 added (`SyncVsAsyncBenchmarkTests`, `TablePerformanceTests`)
has never been run.** No R/.NET installation has been available in
any sandbox this project has run in. Every performance claim in this
project — including "should the R side be rewritten in C," which
remains a prose estimate — is unverified until someone with a real
machine runs them and reports back. **(open)** — highest-priority item
in `phase-8-plan.md`'s ordering, ahead of new design work.

---

## Not a deviation, but adjacent scope boundaries worth restating here

These aren't divergences from spec — they're permanent scope
boundaries spec already implied, restated here because they come up
in the same conversations as the items above:

- **C# never gets a deserializer for R's `serialize()` format** (§7).
  `RValue.SerializedBytes` is meant to be shuttled between R calls
  unexamined, not inspected on the C# side. Deliberate, not a gap.
- **`RTypeConverter`/`RValueConversionExtensions`** (class/collection
  ↔ `RValue` mapping) is a separate, optional convenience layer over
  `RValue` — not part of the wire protocol, and not a substitute for
  direct `RValue` construction on the performance-critical
  bulk-transfer path.
- **`IProcessSupervisor`** (added Phase 8, see
  `docs/phases/phase-8-plan.md`) is a mockable call-surface
  abstraction for host application code, not a full abstraction over
  `ProcessSupervisor`'s internals — `RHandle` still calls back into
  the concrete class regardless of which type a caller holds its
  supervisor through.

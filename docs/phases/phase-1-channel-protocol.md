# Phase 1 — Channel Abstraction & Frame Protocol

Status: not started. See `../progress.md` for overall project status.

## Goal

Introduce the real `IRChannel` abstraction and the length-prefixed
frame codec, with both a synchronous and an asynchronous execution
path over it. Replace Phase 0's placeholder handshake with a proper
`HELLO` frame. Add `PING`/`PONG` heartbeat and `SHUTDOWN`.

## Prerequisites

Phase 0 complete: process launches, raw socket connects both ways.

## Reference

`../spec.md` §2.1 (channel abstraction), §4 (wire protocol), §3.3
(health monitoring), §3.5 (shutdown).

## Checklist

### `IRChannel`
- [ ] Define the interface exactly as in spec §2.1 (`Read`, `Write`,
      `ReadAsync`, `WriteAsync` — all four, no derivation of one from
      another).
- [ ] `SocketRChannel : IRChannel` wrapping `NetworkStream`.
- [ ] Unit test: write N bytes via `Write`, read back via `Read` on a
      loopback pair (no R process involved — pure C# socket pair is
      enough to test the channel implementation in isolation).
- [ ] Same test again via the async members.

### Frame codec (channel-agnostic, pure functions)
- [ ] `FrameHeader` struct: `Length`, `MsgType`, `CorrelationId` per
      spec §4.1. Little-endian throughout — use
      `BinaryPrimitives.WriteInt32LittleEndian` etc., not `BitConverter`
      (avoids platform-endianness footguns).
- [ ] `FrameEncoder`/`FrameDecoder` (or a single `FrameCodec`) taking
      `Span<byte>`/`ReadOnlySpan<byte>` — no `IRChannel` dependency in
      this type at all; it only knows about bytes in, bytes out.
- [ ] `MsgType` enum matching spec §4.2 exactly (values matter — pin
      them in a comment referencing the spec table so the two don't
      drift).
- [ ] Unit tests: encode/decode round-trip for a frame with each
      `MsgType` and a few payload sizes (0 bytes, 1 byte, large).
- [ ] Unit tests: truncated frame (short read), bogus length, unknown
      `MsgType` byte — all must throw a specific, documented exception
      type, never hang or corrupt state for the next read.

### Two execution loops over the codec
- [ ] `RConnection` (sync): blocking read loop, `Send`/`Receive`
      methods built from `IRChannel.Read`/`Write` + the frame codec.
- [ ] `RConnection` (async): `SendAsync`/`ReceiveAsync` built from
      `IRChannel.ReadAsync`/`WriteAsync`. Decide now whether this is a
      second type or the same type with both method sets — spec
      doesn't mandate either, pick one and note the choice in
      progress.md's "Decisions changed since spec.md" if it's not
      obvious from spec.md alone.
- [ ] Consider `System.IO.Pipelines` (`PipeReader`/`PipeWriter`) for
      the async loop now rather than retrofitting later (spec §9)
      — wrapping `NetworkStream` via `StreamPipeReader`/`StreamPipeWriter`
      gets you backpressure and `ReadOnlySequence<byte>` slicing for
      free.

### Real `HELLO`, heartbeat, shutdown
- [ ] Replace Phase 0's placeholder handshake: R sends a proper
      `HELLO` frame (token + R version string as payload) using the
      real frame codec.
- [ ] `PING`/`PONG`: C# sends `PING` on an idle timer; R responds
      `PONG` immediately from the message loop.
- [ ] R-side message loop skeleton: read frame → dispatch on
      `MsgType` → (for now) only `PING` and `SHUTDOWN` need real
      handling; anything else can be a stub that responds `ERROR`
      "not implemented yet" until later phases.
- [ ] `SHUTDOWN`: C# sends the frame, R exits its loop and process
      cleanly; `ProcessSupervisor.Dispose` (from Phase 0) now tries
      this before falling back to `Process.Kill`.
- [ ] Heartbeat timeout → the (still-informal) `Faulted` state; log it
      clearly even before Phase 6's full recovery logic exists.

## Exit criteria (from spec.md §11 Phase 1)

- [ ] Heartbeat keeps the connection alive over both sync and async
      call paths in a running test.
- [ ] Killing the R process externally (e.g. `Process.Kill` from the
      test itself, simulating an external kill) is detected within
      one heartbeat interval.
- [ ] `SHUTDOWN` results in clean process exit, verified via exit
      code.

## Notes for resuming mid-phase

The frame codec is the piece most worth getting right before moving
on — Phases 2–5 all build directly on it. If resuming here, prioritize
finishing the truncated/corrupt-frame tests before adding new message
types; those tests catch a class of bug that's much more annoying to
debug once real data types are flowing through the same code path.

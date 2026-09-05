# Phase 0 — Skeleton & Handshake

Status: not started. See `../progress.md` for overall project status.

## Goal

Get an `RScript.exe` process launched by C#, connected back over a
TCP loopback socket, and torn down cleanly — no protocol, no data
transfer yet. This proves the process-launch + connect-back plumbing
works before anything else is built on top of it.

## Prerequisites

None — this is the starting point.

## Reference

`../spec.md` §3.1 (Launch), §3.2 (state machine), §3.5 (shutdown).

## Checklist

### C# project setup
- [ ] Create the solution: `RWire.sln`, with projects
      `src/RWire/RWire.csproj` (the library) and
      `tests/RWire.Tests/RWire.Tests.csproj`.
- [ ] Target `.NET 10` in both `.csproj` files.
- [ ] `RWire` class library: enable `<Nullable>enable</Nullable>`,
      `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` (needed later for
      `MemoryMarshal`/`Marshal` work, fine to enable now).

### R worker script
- [ ] `r/worker.R` — parses `commandArgs(trailingOnly = TRUE)` for
      `--channel`, `--port`, `--token`.
- [ ] Connects out: `socketConnection(host = "127.0.0.1", port =
      <port>, open = "a+b", blocking = TRUE)` (binary, bidirectional).
- [ ] Sends a minimal `HELLO` — for this phase, a fixed-format line is
      fine (real frame format arrives in Phase 1); just prove
      token + R version can be sent and read. Suggested placeholder:
      write the token bytes back immediately after connecting.
- [ ] Wrap the connect + send in `tryCatch`; on any error, print to
      stderr and exit non-zero (so C# sees a distinguishable failure
      via exit code, not a silent hang).

### C# process launch
- [ ] `ProcessSupervisor` (skeleton only — full state machine is
      Phase 6): binds a loopback `TcpListener` on port 0 (OS-assigned
      ephemeral port), reads back the actual bound port.
- [ ] Generates a random per-session token (e.g. `Guid.NewGuid()` or a
      cryptographically random byte string — doesn't need to be
      security-grade, just unique per launch).
- [ ] Starts `RScript.exe` via `System.Diagnostics.Process`, passing
      `worker.R --channel=socket --port=<port> --token=<token>`.
- [ ] `RedirectStandardOutput = true`, `RedirectStandardError = true`,
      wire up `OutputDataReceived`/`ErrorDataReceived` to a basic
      logger (even `Console.WriteLine` is fine for this phase — a
      real logging abstraction can come later).
- [ ] `TcpListener.AcceptTcpClientAsync()` (with a timeout — if R
      never connects, this must not hang forever; a few seconds is a
      reasonable Phase-0 timeout) to accept the connection back from
      R.
- [ ] Validate the token read back from R matches what was generated;
      mismatch or timeout → treat as failed startup.
- [ ] On successful match: state is `Ready` (informally for now — the
      full `NotStarted → Starting → Ready → ...` enum arrives in
      Phase 6, but it's fine to define the enum now if convenient).

### Shutdown
- [ ] `IDisposable`/`IAsyncDisposable` on the supervisor: closes the
      socket, then attempts `Process.Kill(entireProcessTree: true)`
      if the process hasn't exited, then disposes the `Process`
      object. Graceful `SHUTDOWN` frame doesn't exist yet (Phase 1) —
      for Phase 0, killing directly on Dispose is acceptable.

## Exit criteria (from spec.md §11 Phase 0)

- [ ] Process launches successfully from a C# test.
- [ ] Handshake (token round-trip) completes.
- [ ] Calling Dispose shuts the R process down (verify via
      `Process.HasExited` after a short wait).
- [ ] A test that intentionally points at a non-existent R
      installation (or bad script path) fails fast with a clear
      exception rather than hanging.

## Notes for resuming mid-phase

If you're picking this phase back up: check whether `worker.R` and
`ProcessSupervisor` both exist and whether the handshake test passes
before writing new code — don't assume "phase not checked off in
progress.md" means "nothing exists yet."

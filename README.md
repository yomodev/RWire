# RWire

A high-performance C# ↔ R interop layer: a .NET client that manages
the lifecycle of an `Rscript` worker process and exchanges data with
it over a custom binary protocol — no Apache Arrow, no C code on the R
side.

**Start here:** [`docs/progress.md`](docs/progress.md) — tracks which
phase is current and what's actually implemented vs. still open. If
you're resuming this project in a new session, read that file before
anything else.

Full design: [`docs/spec.md`](docs/spec.md) (and
[`docs/spec-deviations.md`](docs/spec-deviations.md) for everywhere
implementation has diverged from it). Per-phase implementation guides
and the Phase 8 forward plan: [`docs/phases/`](docs/phases/).

## Status

Phases 1–6 are implemented and confirmed working (manual build/test).
Phase 7 (performance hardening) is implemented but has not yet been
built or run in any environment. Phase 8 (feedback-driven hardening)
is in progress. See `docs/progress.md` for the authoritative
phase-by-phase breakdown, and `docs/phases/phase-8-plan.md` for what's
next.

## Requirements

- .NET 10 SDK
- R ≥ 4.4, with `Rscript` on PATH
- `data.table` installed on the R side (`install.packages("data.table")`)

## Building

```
dotnet build
```

## Testing

Most tests are integration tests that launch a real R process — they
require `Rscript` to be resolvable on PATH.

```
dotnet test
```

### Cross-platform (Linux) verification

A `Dockerfile` at the repo root builds and tests RWire on Linux
(`.NET 10 SDK` base image + `r-base-core` + CRAN `data.table`). It has
not been built or run anywhere yet — see `docs/phases/phase-8-plan.md`
for status. To try it:

```
docker build -t rwire-verify .
docker run --rm rwire-verify
```

## Usage

The main entry point is `ProcessSupervisor`. It owns the R worker
process end-to-end: starting it, completing the handshake, keeping it
alive with a heartbeat, and restarting it automatically if it crashes.

```csharp
using RWire;

var options = new RWireOptions
{
    WorkerScriptPath = "path/to/r/worker.R",
};

using var supervisor = new ProcessSupervisor(options);
await supervisor.StartAsync();

// Evaluate an R expression and get the result back as an RValue.
RValue result = await supervisor.EvalAsync("1:10");
int[] values = result.IntegerValues!;

// Call a named R function with inline arguments.
RValue sum = await supervisor.CallAsync(
    "sum",
    new RCallArgument[] { RValue.OfInteger([1]), RValue.OfInteger([2]) });

// Keep a large object on the R side and reference it by handle,
// instead of round-tripping its data on every call.
RHandle handle = await supervisor.SetObjAsync(myLargeRValue);
RValue processed = await supervisor.CallAsync("process_data", new RCallArgument[] { handle });
handle.Dispose(); // releases the R-side reference
```

`ProcessSupervisor` implements `IProcessSupervisor`, which exposes the
same Eval/Call/SetObj/GetObj/CreateRef surface plus status/diagnostics
(`State`, `SessionId`, `RecentDiagnosticOutput`, the `DiagnosticOutput`
event, etc.). Application code that wants to depend on "something that
can drive an R session" for its own unit tests should depend on
`IProcessSupervisor` rather than the concrete class. Note this is a
mockable call surface, not a full abstraction over internals —
`RHandle` is still tied to the concrete `ProcessSupervisor` regardless
of which type you hold it through; see
`docs/phases/phase-8-plan.md`'s "IProcessSupervisor design notes" for
why and when that would be worth revisiting.

### Logging

`ProcessSupervisor` accepts an optional `ILogger<ProcessSupervisor>`
via its constructor. RWire never chooses a logging backend for you —
pass in whatever your host application already uses (Serilog, NLog,
the built-in console/file providers). If omitted, logging is a no-op.

```csharp
using Microsoft.Extensions.Logging;

ILogger<ProcessSupervisor> logger = loggerFactory.CreateLogger<ProcessSupervisor>();
using var supervisor = new ProcessSupervisor(options, logger);
```

### Handling faults and restarts

A crash or unresponsive worker automatically triggers a restart with
exponential backoff (`RWireOptions.MaxRestartAttempts`,
`InitialRestartDelay`, `MaxRestartDelay`). This is transparent to
callers in the common case: the next call after a crash blocks briefly
for the restart to settle rather than throwing immediately. A call
that was itself in flight when the crash happened still fails with a
clear exception — only the *next* call benefits from the
already-recovered connection. Check `IsPermanentlyFailed` and
`LastFault` if you need to detect that automatic recovery has been
exhausted.

### Custom channels

The default constructor uses a TCP loopback socket. Pass a
`Func<IRChannelListener>` to the other constructor overload to use a
different channel implementation (e.g. named pipes, once one exists)
without any protocol-level changes.

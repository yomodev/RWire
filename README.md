# RWire

A high-performance C# ↔ R interop layer: a .NET client that manages
the lifecycle of an `RScript.exe` worker process and exchanges data
with it over a custom binary protocol.

**Start here:** [`docs/progress.md`](docs/progress.md) — tracks which
phase is current and what's actually implemented vs. still open. If
you're resuming this project in a new session, read that file before
anything else.

Full design: [`docs/spec.md`](docs/spec.md). Per-phase implementation
guides: [`docs/phases/`](docs/phases/).

## Status

Phase 0 (skeleton & handshake) — implementation in progress. See
`docs/progress.md`.

## Requirements

- .NET 10 SDK
- R ≥ 4.4, with `Rscript` on PATH

## Building

```
dotnet build
```

## Testing

Phase 0's tests are integration tests that launch a real R process —
they require `Rscript` to be resolvable on PATH.

```
dotnet test
```

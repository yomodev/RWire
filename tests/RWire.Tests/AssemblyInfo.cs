using Xunit;

// Every test class without an explicit [Collection] attribute is its
// own implicit xunit collection, and xunit parallelizes collections
// against each other by default. That's fine for ordinary unit tests,
// but most of this suite launches a real Rscript process per test (or
// per class, via RWireProcessFixture) and relies on tight, deliberately
// fast heartbeat/restart timing budgets (HeartbeatInterval=300ms,
// HeartbeatResponseTimeout=2s in most restart/cancellation tests -
// see ProcessSupervisorTests.FastHeartbeatOptions/FastRestartOptions)
// to keep the suite's wall-clock time reasonable.
//
// Running a dozen-plus test classes' worth of real R subprocesses,
// socket I/O, and heartbeat loops all at once contends hard enough for
// CPU/scheduling that those tight budgets can genuinely blow past
// their margins under real (not simulated) load - this is the
// most likely explanation for the flaky/timing-sensitive test(s)
// tracked in docs/progress.md and docs/phases/phase-8-plan.md across
// several phases without ever being pinned down: nothing was wrong
// with any single test in isolation, but running the whole suite in
// parallel against itself created exactly the kind of system-wide
// contention those tight timing budgets weren't written to tolerate.
//
// Disabling collection parallelization trades a slower total test-run
// time for determinism - the right trade for this suite, since
// correctness of the restart/heartbeat logic is the entire point of
// several of these tests, not the suite's raw speed. If a test still
// flakes with this in place, that's much stronger evidence of a real
// production bug (see docs/progress.md's "Notes / blockers" for what
// to capture if it recurs) rather than environmental contention.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

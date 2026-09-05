using System.Runtime.CompilerServices;

// Lets the test project reach internal test-only members (e.g.
// ProcessSupervisor.ProcessForTesting) without making them part of
// the public API surface.
[assembly: InternalsVisibleTo("RWire.Tests")]

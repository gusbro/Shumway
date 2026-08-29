// Test collections RUN IN PARALLEL here — deliberately. The engine is
// advertised as multi-engine and thread-agile (single-threaded activations,
// thread-safe global tables), and this suite is where that claim gets
// exercised for real: a few engines living concurrently on different threads
// of one process. The old blanket DisableTestParallelization blamed the
// AtomTable/FunctorTable, which are thread-safe by invariant; the actual
// process-wide mutators are cataloged, not assumed — classes that swap
// Console.Error, set environment variables, change the current directory, or
// assert equality on process-global counters carry [Collection("exclusive")]
// (serialized among themselves) plus [Trait("Concurrency", "exclusive")] so
// the gate script can run them in their own serial phase, apart from the
// parallel phase entirely.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 3)]

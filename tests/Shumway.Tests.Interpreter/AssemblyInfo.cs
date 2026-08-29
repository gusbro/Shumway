// Collections run in parallel: the engine is multi-engine and thread-agile,
// and the suite doubles as the exercise of that claim. The old serial setting
// blamed the process-global AtomTable/FunctorTable, which are thread-safe by
// invariant; this project has no destructive test hooks and no process-state
// mutators (cataloged 2026-08: env vars, cwd, Console swaps, ==-asserts on
// global counters — none here).
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 3)]

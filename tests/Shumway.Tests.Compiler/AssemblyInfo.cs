// Collections run in parallel: the engine is multi-engine and thread-agile,
// and the suite doubles as the exercise of that claim. The old serial setting
// was cargo-culted from Tests.Core, whose real reason is its destructive
// ResetForTesting hooks — this project has none, and no process-state
// mutators either (cataloged 2026-08: env vars, cwd, Console swaps,
// ==-asserts on global counters — none here; the `static int Count(...)`
// matches are pure helper functions).
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 3)]

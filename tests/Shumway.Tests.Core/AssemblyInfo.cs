// Serial NOT because the engine can't take parallelism — the AtomTable and
// FunctorTable are thread-safe by invariant, and Shumway.Tests.Embedding runs
// its collections concurrently precisely to exercise multi-engine
// thread-agility. This assembly stays serial because AtomTableTests and
// FunctorTableTests call ResetForTesting() in their constructors: a
// test-only hook that WIPES the process-global tables, which no amount of
// table thread-safety survives while a neighbour interns. The whole suite
// runs in under a second, so serializing it costs nothing; if that changes,
// the fix is a two-phase split (parallel population + the two destructive
// classes alone), not re-enabling parallelism wholesale.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

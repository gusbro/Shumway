// The engine-backed tests here (EngineWasmTierTests) run live PrologEngines,
// which share the process-wide AtomTable / FunctorTable statics -- the same
// reason in-process xUnit parallelism is disabled for Shumway.Tests.Embedding.
// A concurrent class interning functors moves FunctorTable.Count under a
// running engine's feet. Serialize the whole assembly; it is small.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

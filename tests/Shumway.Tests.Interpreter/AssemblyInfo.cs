// Mirrors Shumway.Tests.Core: disable xUnit's default cross-class parallel execution
// because AtomTable and FunctorTable are process-global per ADR-001. The Interpreter
// suite touches the same tables transitively through Activation.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

// Same discipline as the other test projects: the global FunctorTable /
// AtomTable that this project's tests touch through PrologEngine are
// process-wide and can't safely be exercised in parallel.
//
// This project was missing it, and the shape of the bug is why the gap went
// unnoticed for so long: with no engine directory configured every test was a
// no-op, so there was nothing to collide. Configure all three dialects and the
// Trealla validation fails beside the others and passes on its own.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

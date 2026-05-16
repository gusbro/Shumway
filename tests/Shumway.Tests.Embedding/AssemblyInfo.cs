// Same discipline as the other test projects: the global FunctorTable /
// AtomTable that this project's tests touch through PrologEngine are
// process-wide and can't safely be exercised in parallel.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

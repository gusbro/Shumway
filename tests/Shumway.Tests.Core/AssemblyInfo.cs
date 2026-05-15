// Shumway's AtomTable and FunctorTable are intentionally process-global per ADR-001.
// xUnit runs test classes in parallel by default; without this attribute, tests in
// different classes that mutate or read these tables race with each other (notably
// FunctorTableTests.ResetForTesting vs. CompoundUnifyTests.Intern). Disabling parallel
// execution serialises everything in this assembly. The whole Core suite currently
// runs in well under a second, so the speed cost is negligible; once it grows enough
// to matter we can switch to a finer-grained xUnit Collection for the global-state
// classes only.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

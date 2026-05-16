// Match the discipline used in Tests.Core / Tests.Interpreter: even though the
// Lexer is stateless and safe to exercise in parallel, sister test classes that
// land here later (FunctorTable interning, AtomTable, etc.) are not.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>abort/0 called from a predicate running in the module is a
/// builtin call like any other: the host gets the abort, and the cleanup of
/// the scope it leaves still runs, as on the other tiers.</summary>
public sealed class AbortInTheModuleTests
{
    [Fact]
    public void TheHostGetsTheAbortAndTheCleanupRuns()
    {
        var (engine, members) = TieredEngine.Build("""
            :- dynamic(cleaned/1).
            down(0) :- abort.
            down(N) :- N > 0, N1 is N - 1, down(N1).
            run :- setup_call_cleanup(true, down(20), assertz(cleaned(yes))).
            """);

        Assert.Throws<PrologAbortException>(() => engine.Query("run."));
        Assert.True(engine.Query("cleaned(yes).").Success);
        var promoted = members.Select(m => FunctorTable.Lookup(m.Predicate.FunctorId))
            .Select(f => $"{AtomTable.GetById(f.AtomId)?.Name}/{f.Arity}").ToList();
        Assert.True(promoted.Exists(n => n.EndsWith("$down/1")), "down/1 did not promote to the module");
    }
}

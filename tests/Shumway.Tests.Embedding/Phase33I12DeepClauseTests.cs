using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 I12 — compiling a clause with a deeply-nested argument (a long
/// list) no longer overflows the host at compile time. Several per-term-node
/// recursions in <c>ClauseCompiler</c> — permanent classification, head-var
/// collection, the argument scheduler's forced-save / live-Y walks, and the
/// ADR-020 reserve-build eligibility checks — used one C# frame per element and
/// stack-overflowed uncatchably on <c>assertz</c> of a large list (crashed at
/// ~3000 elements). All are now iterative.
///
/// <para>A crash here aborts the whole test host, so the real assertion is
/// "the process survives and the clause compiled + dispatches".</para>
/// </summary>
public class Phase33I12DeepClauseTests
{
    // Depth well past the former ~3000-element overflow, small enough to stay fast.
    private const int Deep = 20000;

    private static PrologEngine NewEngine()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic store/1.
            :- dynamic ran/0.
            sink(_).
            mk(0, []) :- !.
            mk(N, [N|T]) :- N > 0, N1 is N-1, mk(N1, T).
            :- public assert_fact/1.
            assert_fact(N) :- mk(N, L), assertz(store(L)).
            :- public assert_rule/1.
            assert_rule(N) :- mk(N, L), assertz((ran :- sink(L))).
            """);
        return e;
    }

    [Fact]
    public void AssertFact_WithDeepListArg_CompilesAndDispatches()
    {
        var e = NewEngine();
        Assert.True(e.Query($"assert_fact({Deep}).").Success);
        // The stored clause compiled and dispatches — read it back and measure.
        Assert.True(e.Query($"store(L), length(L, {Deep}).").Success);
    }

    [Fact]
    public void AssertRule_WithDeepListBodyArg_CompilesAndRuns()
    {
        var e = NewEngine();
        Assert.True(e.Query($"assert_rule({Deep}).").Success);
        // The rule compiled (deep list in a body goal argument) and runs.
        Assert.True(e.Query("ran.").Success);
    }

    [Fact]
    public void ConsultedFactWithModerateListArg_StillWorks()
    {
        // A sanity check that ordinary (shallow) list clauses are unaffected.
        var e = new PrologEngine();
        e.ConsultString("""
            p([a, b, c, [1, 2, 3], d]).
            :- public q/0.
            q :- p([a, b, c, [1, 2, 3], d]).
            """);
        Assert.True(e.Query("q.").Success);
    }
}

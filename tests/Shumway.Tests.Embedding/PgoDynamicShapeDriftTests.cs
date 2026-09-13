using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The two-phase PGO recompile reorders an indexed-atom predicate's
/// ground dispatch by measured hit counts. Phase 1 allocates a profile key on
/// the shape it PROMOTED; phase 2 recompiles the predicate the query's program
/// holds for that functor id. For a DYNAMIC predicate those differ: phase 1
/// profiled the ADR-023 static snapshot, but the program's entry is the
/// dynamic-dispatch form (enter_dynamic + check_visible), which is not
/// IL-compilable -- so phase 2 threw NotSupportedException on the compile
/// worker and the exception surfaced out of the next query.
///
/// <para>gensym/2 is the everyday trigger: it asserts <c>$gensym_base/1</c>
/// facts, so a program that calls it in a hot loop (call_nth/2 does) promotes
/// and profiles $gensym_base, then mutates it, and the stale profile key drove
/// a recompile of the dynamic form. A shape that no longer IL-compiles now
/// drops its profile and keeps the installed delegate.</para></summary>
public sealed class PgoDynamicShapeDriftTests
{
    /// <summary>The exact crash: gensym in a promoted loop, synchronous
    /// compilation so a phase-2 failure would propagate to the query. Before
    /// the fix this threw "Multi-clause predicate ... outside the IL subset".</summary>
    [Fact]
    public void GensymInAPromotedLoopDoesNotCrashThePgoRecompile()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = 32;
        e.IlPromotion.PgoSampleThreshold = 32;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString("""
            step(Goal) :- gensym('$k', _), call(Goal).
            loop(0) :- !.
            loop(N) :- step(true), M is N - 1, loop(M).
            nth(0) :- !.
            nth(N) :- call_nth(true, 1), M is N - 1, nth(M).
            """);
        // Both loops in one query, as the original crash did: gensym and
        // call_nth each mutate $gensym_base, so its profile key outlives the
        // shape it was recorded on and drives a recompile of the dynamic form.
        for (int i = 0; i < 60; i++)
            Assert.True(e.Query("loop(40), nth(2).").Success);
        // And the engine still works afterwards -- gensym still hands out
        // fresh, distinct atoms.
        var a = e.QueryFirst<string>("gensym('$k', X).", "X");
        var b = e.QueryFirst<string>("gensym('$k', X).", "X");
        Assert.NotEqual(a, b);
    }

    /// <summary>call_nth/2 specifically -- the predicate whose corruption first
    /// surfaced this, since it gensyms a fresh counter key per call.</summary>
    [Fact]
    public void CallNthInAPromotedLoopSurvivesPgo()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = 32;
        e.IlPromotion.PgoSampleThreshold = 32;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString("""
            nth(0) :- !.
            nth(N) :- call_nth(true, 1), M is N - 1, nth(M).
            """);
        for (int i = 0; i < 60; i++)
            Assert.True(e.Query("nth(50).").Success);
    }

    /// <summary>ANTI-VACUITY: a STATIC indexed-atom predicate must still take
    /// the optimized recompile -- the fix declines only the shapes that cannot
    /// compile, not every recompile. This one compiles and PGO-optimizes, and
    /// keeps answering correctly through both phases.</summary>
    [Fact]
    public void AStaticIndexedAtomPredicateStillPgoOptimizes()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = 1;
        e.IlPromotion.PgoSampleThreshold = 4;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString("""
            :- public col/1.
            col(red). col(green). col(blue). col(yellow).
            """);
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("col").Id, 1);
        // Ground queries drive the profile; the phase-2 recompile fires once
        // the sample count crosses the threshold. The guard must NOT decline
        // this -- it is a static, IL-compilable indexed-atom predicate.
        for (int i = 0; i < 12; i++) Assert.True(e.Query("col(blue).").Success);
        Assert.True(e.IlPromotion.IsPgoOptimized(fid),
            "a static indexed-atom predicate was denied its PGO recompile");
        // Every phase answers the same: known atoms succeed, unknowns fail.
        Assert.True(e.Query("col(red).").Success);
        Assert.True(e.Query("col(yellow).").Success);
        Assert.False(e.Query("col(purple).").Success);
    }
}

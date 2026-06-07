using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 339 (Phase 28): the Tier-1 IL compiler must flush pending attribute
/// wakeups before an IL-emitted cut — the IL counterpart of the chunk-335
/// bytecode fix.
///
/// <para>A cut is a goal boundary. Binding a clpfd attributed variable to a
/// value queues a <c>verify_attributes</c> wakeup (the domain check is deferred
/// to the next goal boundary) and the unify returns success. The bytecode
/// interpreter flushes that wakeup before a Cut/NeckCut commits; the IL compiler
/// used to emit <c>engine.NeckCut</c> / <c>CutToLevel</c> directly with no
/// flush, so an IL-promoted predicate that bound an attvar and then cut would
/// commit before the (failing) constraint ran, leaving no choice point to
/// backtrack into. The fix emits <c>engine.FlushWakeupsForIlCut()</c> before the
/// cut and branches to the clause fail label on a failed wakeup.</para>
/// </summary>
public class Chunk339Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // Deep cut (get_level + cut): the `!` follows the body goal `X = V`, which
    // binds the clpfd attvar X. choose/2's first clause binds X to the list
    // head then cuts its recursive alternative. With X in 1..5 and head 9, the
    // wakeup (9 not in 1..5) must flush+fail BEFORE the cut prunes the
    // recursive clause, so backtracking reaches V=3 → X=3.
    [Fact]
    public void IlDeepCut_FlushesFailingWakeup_BeforePruningAlternative()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public choose/2.\n"
            + "choose(X, [V | _]) :- X = V, !.\n"
            + "choose(X, [_ | T]) :- choose(X, T).\n");

        for (int i = 0; i < 6; i++)
        {
            var sol = engine.Query("X in 1..5, choose(X, [9, 3, 4]).");
            Assert.True(sol.Success);
            Assert.Equal(new IntTerm(3), sol["X"]);
        }

        Assert.True(engine.IlPromotion.IsPromoted(Fid("choose", 2)));
    }

    // Neck cut: the head `m(9, a)` binds the clpfd attvar X to 9 (queuing a
    // failing wakeup), and the `!` is the first body goal. Without the flush the
    // neck cut prunes the `m(_, b)` clause before the wakeup runs, so the whole
    // call fails; with the flush the wakeup fails first, clause 1 backtracks,
    // and clause 2 gives R = b (X stays constrained, never bound to 9).
    [Fact]
    public void IlNeckCut_FlushesFailingWakeup_BeforePruningSiblingClause()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public m/2.\n"
            + "m(9, a) :- !.\n"
            + "m(_, b).\n");

        for (int i = 0; i < 6; i++)
        {
            var sol = engine.Query("X in 1..5, m(X, R).");
            Assert.True(sol.Success);
            Assert.Equal(new AtomTerm("b"), sol["R"]);
        }

        Assert.True(engine.IlPromotion.IsPromoted(Fid("m", 2)));
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 109 (Phase 7): well-founded negation. A program with tabled
/// negation is evaluated by the alternating fixpoint, so negative cycles
/// terminate: the atoms in a cycle become <em>undefined</em> (the third
/// truth value) instead of looping forever. <c>well_founded/2</c> reports
/// a tabled goal's value — true, false or undefined.
/// </summary>
public class Chunk109Tests
{
    private static PrologEngine WithProgram(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    [Fact]
    public void NegativeSelfCycle_IsUndefinedAndTerminates()
    {
        // p :- \+ p — the canonical undefined atom. Under plain SLD this
        // loops; under WFS it terminates with p undefined.
        var engine = WithProgram("""
            :- table p/0.
            p :- \+ p.
            """);
        Assert.True(engine.Query("well_founded(p, undefined).").Success);
        Assert.False(engine.Query("p.").Success);   // undefined is not true
    }

    [Fact]
    public void NegativeTwoCycle_BothUndefined()
    {
        var engine = WithProgram("""
            :- table p/0.
            :- table q/0.
            p :- \+ q.
            q :- \+ p.
            """);
        Assert.True(engine.Query("well_founded(p, undefined).").Success);
        Assert.True(engine.Query("well_founded(q, undefined).").Success);
    }

    [Fact]
    public void DeterminedThroughAnotherClause_DespiteCycle()
    {
        // p is in a p/q negative cycle, but p also has an unconditional
        // clause — so p is true, and therefore q (= \+ p) is false.
        var engine = WithProgram("""
            :- table p/0.
            :- table q/0.
            base.
            p :- base.
            p :- \+ q.
            q :- \+ p.
            """);
        Assert.True(engine.Query("well_founded(p, true).").Success);
        Assert.True(engine.Query("well_founded(q, false).").Success);
        Assert.True(engine.Query("p.").Success);
        Assert.False(engine.Query("q.").Success);
    }

    [Fact]
    public void GameWinLoseDraw()
    {
        // win(X) :- move(X,Y), \+ win(Y). The a<->b cycle is a draw
        // (undefined); c wins (moves to the dead end d); d loses.
        var engine = WithProgram("""
            :- table win/1.
            move(a, b).  move(b, a).  move(c, d).
            win(X) :- move(X, Y), \+ win(Y).
            """);
        Assert.True(engine.Query("well_founded(win(c), true).").Success);
        Assert.True(engine.Query("well_founded(win(d), false).").Success);
        Assert.True(engine.Query("well_founded(win(a), undefined).").Success);
        Assert.True(engine.Query("well_founded(win(b), undefined).").Success);
    }

    [Fact]
    public void StratifiedNegation_AgreesWithTwoValuedModel()
    {
        // No negative cycle — WFS coincides with the stratified model.
        var engine = WithProgram("""
            :- table good/1.
            :- table bad/1.
            num(1).  num(2).  num(3).
            bad(2).
            good(X) :- num(X), \+ bad(X).
            """);
        Assert.Equal(2, engine.QueryAll("good(X).").Count());   // 1, 3
        Assert.True(engine.Query("well_founded(good(1), true).").Success);
        Assert.True(engine.Query("well_founded(good(2), false).").Success);
    }
}

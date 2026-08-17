using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 335 (Phase 28): a cut is a goal boundary and must flush pending
/// attribute-hook wakeups BEFORE committing.
///
/// <para>Binding a clpfd attributed variable to a value (plain <c>=/2</c>)
/// queues a <c>verify_attributes/4</c> wakeup — the domain check is deferred
/// to the next goal boundary, and the unification itself returns success. In
/// an <c>(Cond -&gt; Then ; Else)</c> the next thing after Cond's last goal is
/// the <c>-&gt;</c> commit cut, which used to prune the inner disjunction's
/// choice point (and the else CP) <em>before</em> the pending wakeup ran. When
/// the wakeup then failed there were no choice points left to backtrack into —
/// an unsound whole-goal failure. The bare query (no cut) always worked.</para>
///
/// <para>Fix: <c>Opcode.Cut</c> / <c>Opcode.NeckCut</c> flush pending wakeups
/// first, exactly like Call/Proceed/Deallocate; a failed flush backtracks
/// instead of cutting.</para>
/// </summary>
public class Chunk335Tests
{
    private static Term Int(long v) => new IntTerm(v);

    private static PrologEngine Fd()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        return engine;
    }

    // The canonical minimal repro: a clpfd attvar unified with an integer in
    // the SECOND branch of a disjunction, the whole thing inside an
    // if-then-else condition. Branch 1 narrows X to 1..2, X=5 fails there;
    // backtracking must reach branch 2 (true, domain restored to 1..9) so
    // X=5 succeeds and the THEN branch runs.
    [Fact]
    public void CutFlushesWakeup_DisjunctionInIfThenElseCondition_TakesThenBranch()
    {
        var sol = Fd().Query(
            "( (X in 1..9, ( X #< 3 ; true ), X = 5) -> R = yes ; R = no ).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("yes"), sol["R"]);
        Assert.Equal(Int(5), sol["X"]);
    }

    // The bare (cut-free) form already worked — guard against a regression
    // that would "fix" the cut path by breaking the ordinary one.
    [Fact]
    public void BareDisjunction_BacktracksToSecondBranch_BindsValue()
    {
        var sol = Fd().Query("X in 1..9, ( X #< 3 ; true ), X = 5.");
        Assert.True(sol.Success);
        Assert.Equal(Int(5), sol["X"]);
    }

    // The genuinely-impossible case must still fail: no disjunction escape
    // hatch, X pinned below 3, then X=5.
    [Fact]
    public void Unsatisfiable_DomainExcludesValue_StillFails()
    {
        var sol = Fd().Query(
            "( (X in 1..9, X #< 3, X = 5) -> R = yes ; R = no ).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("no"), sol["R"]);
    }

    // once/1 is implemented with a cut: backtracking inside it after a
    // constraint binding fails must reach the next member solution before the
    // cut commits. V=7 fails (7 not in 1..5), V=3 gives X=3.
    [Fact]
    public void OnceOverConstraint_BacktracksInsideCutBarrier()
    {
        var sol = Fd().Query(
            "X in 1..5, once((member(V, [7, 3]), X #= V)).");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["X"]);
    }

    // Neck-cut path: a clause whose head/early body binds the attvar, then a
    // neck cut, then the constraint must still be able to fail-and-retry the
    // caller's alternatives.
    [Fact]
    public void NeckCut_OverConstraintInClause_RetriesCaller()
    {
        var engine = Fd();
        engine.ConsultString("pick(X) :- member(V, [7, 3]), X #= V, !.");
        var sol = engine.Query("X in 1..5, pick(X).");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["X"]);
    }
}

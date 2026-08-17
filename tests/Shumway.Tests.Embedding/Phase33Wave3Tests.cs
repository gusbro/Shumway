using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 wave 3 — WAM codegen items (docs/phase-33-backlog.md, W-series).
/// W1: once/ignore compile to a synthesized '$once_N'/'$ign_N' helper (snips!).
/// W2: a `!` preceded only by inline guards is a frameless neck cut.
/// </summary>
public class Phase33Wave3Tests
{
    // ---- W1: once/1 ----

    [Fact]
    public void W1_Once_CommitsToFirstSolution_AndBinds()
    {
        var e = new PrologEngine();
        e.ConsultString("p.");
        Assert.True(e.Query("once(member(X, [a, b, c])), X == a.").Success);
        // Exactly one solution — once is not re-satisfiable.
        Assert.True(e.Query("findall(X, once(member(X, [a, b, c])), L), L == [a].").Success);
    }

    [Fact]
    public void W1_Once_IsACutBarrier_OuterChoicePointsSurvive()
    {
        var e = new PrologEngine();
        e.ConsultString("p.");
        // The commit inside once must not prune the outer member's alternatives.
        Assert.True(e.Query(
            "findall(X, (member(X, [1, 2]), once(member(_, [y, z]))), L), L == [1, 2].").Success);
        // A goal that backtracks INTERNALLY before committing still works, and an
        // explicit `!` inside the once'd goal stays scoped to the once barrier.
        Assert.True(e.Query(
            "findall(Y, (member(Y, [1, 2]), once((member(X, [a, b]), X == b))), L), L == [1, 2].").Success);
    }

    [Fact]
    public void W1_Once_FailingGoal_Fails()
    {
        var e = new PrologEngine();
        e.ConsultString("p.");
        Assert.False(e.Query("once(fail).").Success);
        Assert.False(e.Query("once(member(x, [a, b])).").Success);
    }

    [Fact]
    public void W1_Ignore_SucceedsEitherWay_AndKeepsBindings()
    {
        var e = new PrologEngine();
        e.ConsultString("p.");
        Assert.True(e.Query("ignore(fail).").Success);
        Assert.True(e.Query("ignore(X = 1), X == 1.").Success);
        // ignore commits (no resatisfaction) and doesn't prune outer CPs.
        Assert.True(e.Query(
            "findall(Y, (member(Y, [1, 2]), ignore(member(_, [a, b]))), L), L == [1, 2].").Success);
    }

    [Fact]
    public void W1_Once_VariableGoal_TakesRuntimePath()
    {
        // A var goal is NOT rewritten (falls to the prelude once/1 + call/1,
        // which raises the ISO errors for non-callables).
        var e = new PrologEngine();
        e.ConsultString("p.");
        Assert.True(e.Query("G = member(X, [a]), once(G), X == a.").Success);
        Assert.True(e.Query(
            "catch(once(1), error(type_error(callable, _), _), R = caught), R == caught.").Success);
    }

    [Fact]
    public void W1_Snips_DesugarToOnce_AndCompile()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            pick(X) :- [! member(X, [a, b, c]) !].
            """);
        // The snip commits to the first solution; bindings flow out.
        Assert.True(e.Query("pick(X), X == a.").Success);
        Assert.True(e.Query("findall(X, pick(X), L), L == [a].").Success);
    }

    // ---- W2: neck cut after inline guards ----

    [Fact]
    public void W2_CutAfterArithGuard_CommitsLikeANeckCut()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            max(X, Y, X) :- X >= Y, !.
            max(_, Y, Y).
            """);
        Assert.True(e.Query("max(3, 2, M), M == 3.").Success);
        Assert.True(e.Query("max(1, 2, M), M == 2.").Success);
        // The cut actually pruned clause 2: without it max(3,2,M) would also
        // yield M = 2 on backtracking.
        Assert.True(e.Query("findall(M, max(3, 2, M), L), L == [3].").Success);
        Assert.True(e.Query("findall(M, max(2, 2, M), L), L == [2].").Success);
    }

    [Fact]
    public void W2_CutAfterGuard_WithBodyAfterCut()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            classify(X, pos) :- X > 0, !, true.
            classify(0, zero) :- !.
            classify(_, neg).
            """);
        Assert.True(e.Query("classify(5, C), C == pos.").Success);
        Assert.True(e.Query("classify(0, C), C == zero.").Success);
        Assert.True(e.Query("classify(-3, C), C == neg.").Success);
        Assert.True(e.Query("findall(C, classify(7, C), L), L == [pos].").Success);
    }

    [Fact]
    public void W2_ChainedGuardsAndCuts()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            band(X, low)  :- X < 10, !.
            band(X, mid)  :- X < 100, !.
            band(_, high).
            """);
        Assert.True(e.Query("band(5, B), B == low.").Success);
        Assert.True(e.Query("band(50, B), B == mid.").Success);
        Assert.True(e.Query("band(500, B), B == high.").Success);
        Assert.True(e.Query("findall(B, band(50, B), L), L == [mid].").Success);
    }

    // ---- W9(e): `=/2` widens the neck-cut prefix (both `=/2` lowerings —
    // the inline get_*/unify_* form AND the call_builtin fallback for
    // Y-var/both-nonvar shapes — leave Cp, B0 and the CP stack untouched) ----

    [Fact]
    public void W9e_CutAfterUnifyGuard_CommitsLikeANeckCut()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            kind(X, atomic) :- X = a, !.
            kind(X, pair)   :- X = f(_, _), !.
            kind(_, other).
            """);
        Assert.True(e.Query("kind(a, K), K == atomic.").Success);
        Assert.True(e.Query("kind(f(1, 2), K), K == pair.").Success);
        Assert.True(e.Query("kind(zzz, K), K == other.").Success);
        // The cut really pruned the later clauses.
        Assert.True(e.Query("findall(K, kind(a, K), L), L == [atomic].").Success);
        Assert.True(e.Query("findall(K, kind(f(x, y), K), L), L == [pair].").Success);
    }

    [Fact]
    public void W9e_UnifyGuard_PermanentVarFallback_StillCommits()
    {
        // Y-var `=` lowers to the call_builtin fallback; the `!` after it must
        // still commit correctly (and the pre-cut binding must hold after).
        var e = new PrologEngine();
        e.ConsultString("""
            tagit(X, R) :- T = tag(X), !, R = T.
            tagit(_, none).
            """);
        Assert.True(e.Query("tagit(7, R), R == tag(7).").Success);
        Assert.True(e.Query("findall(R, tagit(7, R), L), L == [tag(7)].").Success);
    }
}

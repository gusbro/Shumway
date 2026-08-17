using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-030 — redundant trailing-cut elimination must be observationally
/// invisible: identical solutions and side effects whether the cut is present or
/// elided. These pin the engine-level soundness of the shipped intra-module pass
/// (default ON). The key hazard is <em>over</em>-eliding a load-bearing cut,
/// which would leak extra solutions on backtracking (<c>extra-backtracking-not-
/// sound</c>).
/// </summary>
public class Adr030RedundantCutTests
{
    private static PrologEngine Consult(string src)
    {
        var e = new PrologEngine();
        e.ConsultString(src);
        return e;
    }

    [Fact]
    public void DetPrefixCut_Elided_StillOneSolution()
    {
        // q/1 single-clause det → the cut in p's only clause is redundant and
        // dropped; p must still yield exactly one solution and commit.
        var e = Consult("q(7). p(X):-q(X),!.");
        Assert.Single(e.QueryAll("p(X)."));
        Assert.True(e.Query("p(X), X == 7.").Success);
    }

    [Fact]
    public void LoadBearingCut_OverMultiClauseCallee_NotOverElided()
    {
        // q/2 backtracks under q(X, L) with L bound and X free. The cut commits
        // to the first — it is load-bearing and MUST survive. If wrongly elided,
        // QueryAll would see two solutions.
        var e = Consult("q(X,[X|_]). q(X,[_|T]):-q(X,T). first(X,L):-q(X,L),!.");
        Assert.Single(e.QueryAll("first(X, [a,b,c])."));   // load-bearing cut kept
        Assert.True(e.Query("first(X, [a,b,c]), X == a.").Success);
    }

    [Fact]
    public void NeckCut_LastClause_ElidedNoBehaviourChange()
    {
        // guard-only prefix (all inline) → neck cut, redundant in the last clause.
        var e = Consult("s(X, pos):-X>0,!. s(_, nonpos).");
        Assert.True(e.Query("s(5, R), R == pos.").Success);
        Assert.Single(e.QueryAll("s(5, R)."));
        Assert.True(e.Query("s(-2, R), R == nonpos.").Success);
        Assert.Single(e.QueryAll("s(-2, R)."));
    }

    [Fact]
    public void FirstArgMultiClauseCallee_CutKept_UnderUnboundCall()
    {
        // The exact soundness hazard: c/1's first-args are distinct, but a call
        // with an unbound arg enumerates both. p's cut commits and must stay.
        var e = Consult("c(a). c(b). p(X):-c(X),!.");
        Assert.Single(e.QueryAll("p(X)."));       // commits to c(a), one solution
        Assert.True(e.Query("p(X), X == a.").Success);
    }

    [Fact]
    public void ElidedTailCall_RecursesCorrectly()
    {
        // len/2 last clause `len([_|T],N):-len(T,M),N is M+1, !.` — the cut is
        // redundant (single applicable recursive clause reached deterministically
        // by first-arg on a bound list), prefix is det → elided → clean tail
        // shape. Result must be unchanged.
        var e = Consult("""
            len([],0).
            len([_|T],N):-len(T,M),N is M+1,!.
            """);
        Assert.True(e.Query("len([a,b,c,d], N), N == 4.").Success);
        Assert.Single(e.QueryAll("len([a,b,c], N)."));
    }
}

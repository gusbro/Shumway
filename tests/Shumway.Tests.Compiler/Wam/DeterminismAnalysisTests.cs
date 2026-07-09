using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// ADR-030 — the intra-module determinism fixpoint and the redundant-trailing-cut
/// rewrite. These pin the *analysis* (which clauses are rewritten and which are
/// left alone); the engine-level soundness proof (identical solution counts with
/// the pass ON) lives in the Embedding suite.
/// </summary>
public class DeterminismAnalysisTests
{
    private static List<Clause> Parse(string src) => new ClauseReader(src).ReadAll().ToList();

    private static List<Clause> Elide(string src) =>
        DeterminismAnalysis.EliminateRedundantTrailingCuts(Parse(src));

    private static Term BodyTerm(Clause c) => ((CompoundTerm)c.Term).Args[1];

    private static bool EndsInCut(Clause c)
    {
        if (c.Kind != ClauseKind.Rule) return false;
        Term body = ((CompoundTerm)c.Term).Args[1];
        while (body is CompoundTerm { Functor: ",", Args.Length: 2 } conj) body = conj.Args[1];
        return body is AtomTerm { Name: "!" };
    }

    [Fact]
    public void LastClause_DetCallPrefix_CutIsElided()
    {
        // `q/1` is a single det clause; the trailing cut in `p`'s last clause
        // therefore prunes nothing → dropped, leaving a clean tail call.
        var outp = Elide("q(1). p(X):-q(X),!.");
        Clause p = outp.Single(c => c.Term is CompoundTerm ct
            && (ct.Functor == ":-" ? ((CompoundTerm)ct.Args[0]).Functor : ct.Functor) == "p");
        Assert.False(EndsInCut(p));
        Assert.IsType<CompoundTerm>(BodyTerm(p));
        Assert.Equal("q", ((CompoundTerm)BodyTerm(p)).Functor);   // single tail call, cut gone
    }

    [Fact]
    public void LastClause_InlinePrefix_NeckCutIsElided()
    {
        // `guard, !.` where the guard is all inline → the cut prunes nothing.
        var outp = Elide("p(X):-X>0,!.");
        Clause p = outp[^1];
        Assert.False(EndsInCut(p));
        Assert.Equal(">", ((CompoundTerm)BodyTerm(p)).Functor);   // just the guard, cut gone
    }

    [Fact]
    public void LastClause_BareCut_BecomesFact()
    {
        var outp = Elide("p:-a. p:-!.");
        Assert.Equal(ClauseKind.Fact, outp[^1].Kind);
        Assert.Equal("p", outp[^1].Term.ToString());
    }

    [Fact]
    public void NondetCallPrefix_CutIsKept()
    {
        // `member/2` backtracks → the cut is load-bearing (commits to the first
        // solution). Dropping it would be UNSOUND. Must be kept.
        var outp = Elide("p(X,L):-member(X,L),!.");
        Assert.True(EndsInCut(outp[^1]));
    }

    [Fact]
    public void NondetUserPredPrefix_CutIsKept()
    {
        // `g/1` has two var-headed non-committing clauses → non-det dispatch (no
        // first-arg key to discriminate) → not in the det set → the cut in `p`'s
        // last clause, which sits after a call to it, is load-bearing.
        var outp = Elide("h. i. g(_):-h. g(_):-i. p(X):-g(X),!.");
        Clause p = outp.Last(c => EndsInCutOrTail(c, "p"));
        Assert.True(EndsInCut(p));
    }

    [Fact]
    public void CrossModuleCallPrefix_CutIsKept()
    {
        // `r/1` is not defined in this module → opaque → conservatively non-det.
        var outp = Elide("p(X):-r(X),!.");
        Assert.True(EndsInCut(outp[^1]));
    }

    [Fact]
    public void NonLastClause_CutIsKept()
    {
        // Only the LAST clause is reached with no clause-alternative CP. Clause 1's
        // cut prunes the CP pointing at clause 2, so it must NOT be elided.
        var outp = Elide("q(1). p(X):-q(X),!. p(_):-fail.");
        Clause first = outp.First(c => c.Term is CompoundTerm { Functor: ":-" } ct
            && ((CompoundTerm)ct.Args[0]).Functor == "p"
            && ((CompoundTerm)ct.Args[0]).Args[0] is not VarTerm { Name: "_" });
        Assert.True(EndsInCut(first));
    }

    [Fact]
    public void MidBodyCut_IsKept()
    {
        // `!` is not the terminal goal (a call follows). Not a redundant trailing
        // cut — the commit-then-continue shape is left for ADR-031.
        var outp = Elide("q(1). p(X):-q(X),!,r(X).");
        // Terminal goal is r(X), not the cut → nothing to elide; body unchanged.
        Assert.True(BodyTerm(outp[^1]) is CompoundTerm { Functor: "," });
        Assert.False(EndsInCut(outp[^1]));   // ends in r(X); the cut stays mid-body
        // The cut is still present somewhere in the body.
        Assert.Contains("!", outp[^1].Term.ToString());
    }

    [Fact]
    public void DisjunctionPrefix_CutIsKept()
    {
        // A `(a;b)` prefix may leave a CP; the model treats `;/2` as non-det.
        var outp = Elide("p(X):-(X=1;X=2),!.");
        Assert.True(EndsInCut(outp[^1]));
    }

    [Fact]
    public void Fixpoint_ChainsDeterminism()
    {
        // a/1 det (single clause) → b/1 det (calls only a, det dispatch) → the cut
        // in c/1's last clause (prefix b/1) is elided.
        var outp = Elide("a(1). b(X):-a(X). c(X):-b(X),!.");
        Clause c = outp[^1];
        Assert.False(EndsInCut(c));
    }

    [Fact]
    public void GuardedClausesPlusCatchAll_IsDet()
    {
        // `p(a):-q(b),!. p(b):-q(a),!. p(_).` — every clause but the LAST commits
        // via a cut; the last (a catch-all fact) needs none (reached via trust).
        // p/1 is deterministic. The pass proves it, enabling a caller's cut to be
        // elided.
        var analysis = DeterminismAnalysis.Build(
            Parse("q(_). p(a):-q(b),!. p(b):-q(a),!. p(_)."));
        Assert.True(analysis.IsDet("p/1"));

        // A caller of the det p/1 gets its own redundant cut dropped.
        var outp = Elide("q(_). p(a):-q(b),!. p(b):-q(a),!. p(_). foo(X):-p(X),!.");
        Clause foo = outp[^1];
        Assert.False(EndsInCut(foo));
    }

    [Fact]
    public void GuardedClausesPlusCatchAllRule_IsDet()
    {
        // Like the catch-all fact, but the last clause is a cut-free RULE whose
        // body is det → still det (last clause needs no cut; its body-det is
        // checked).
        var analysis = DeterminismAnalysis.Build(
            Parse("q(_). p(a):-q(b),!. p(b):-q(a),!. p(_):-q(a),q(b)."));
        Assert.True(analysis.IsDet("p/1"));
    }

    [Fact]
    public void SelfRecursiveLastClause_IsDet_ViaGreatestFixpoint()
    {
        // p(c):-q(a),p(a). The last clause's body contains a RECURSIVE call.
        // p is det because clauses 1-2 commit and clause 3's body is det *given
        // p is det* — the greatest-fixpoint proves it (a least-fixpoint from
        // empty cannot bootstrap the self-reference).
        var analysis = DeterminismAnalysis.Build(
            Parse("q(_). p(a):-q(b),!. p(b):-q(a),!. p(c):-q(a),p(a)."));
        Assert.True(analysis.IsDet("p/1"));
    }

    [Fact]
    public void MutualRecursion_SingleClauses_AreDet()
    {
        // Two single-clause predicates calling each other with det leaves —
        // coinductively det.
        var analysis = DeterminismAnalysis.Build(
            Parse("base(_). a(X):-base(X),b(X). b(X):-base(X),a(X)."));
        Assert.True(analysis.IsDet("a/1"));
        Assert.True(analysis.IsDet("b/1"));
    }

    [Fact]
    public void RecursiveButNondetDispatch_IsNotDet()
    {
        // A recursive predicate whose dispatch is NOT det (an earlier clause does
        // not commit) must stay non-det — the greatest-fixpoint removes it. Here
        // clause 1 leaves a CP to clause 2, so `p(X)` is genuinely non-det.
        var analysis = DeterminismAnalysis.Build(
            Parse("q(_). p(X):-q(X). p(X):-p(X)."));
        Assert.False(analysis.IsDet("p/1"));
    }

    [Fact]
    public void CatchAllNotLast_IsNotDet()
    {
        // If the cut-free catch-all is NOT last, an earlier non-committing clause
        // can leave a CP → not det (and the sound rule rejects it).
        var analysis = DeterminismAnalysis.Build(
            Parse("q(_). p(_). p(a):-q(b),!."));
        Assert.False(analysis.IsDet("p/1"));
    }

    [Fact]
    public void DynamicIneligible_CutIsKept()
    {
        // A predicate flagged ineligible (dynamic) is never proven det and never
        // rewritten — its clause set changes at runtime.
        var clauses = Parse("d(1). p(X):-d(X),!.");
        // `d/1` is ineligible → treated non-det → p's cut kept.
        var outp = DeterminismAnalysis.EliminateRedundantTrailingCuts(
            clauses, isEligible: c => DeterminismAnalysis.HeadIndicator(c) != "d/1");
        Assert.True(EndsInCut(outp[^1]));
        // And d/1 itself is untouched even if it ended in a cut.
        var outp2 = DeterminismAnalysis.EliminateRedundantTrailingCuts(
            Parse("e(1). d(X):-e(X),!."),
            isEligible: c => DeterminismAnalysis.HeadIndicator(c) != "d/1");
        Assert.True(EndsInCut(outp2[^1]));
    }

    private static bool EndsInCutOrTail(Clause c, string name) =>
        c.Term is CompoundTerm { Functor: ":-" } ct
        && ((CompoundTerm)ct.Args[0]).Functor == name;
}

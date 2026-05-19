using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 69 — IL inlining of small leaf callees (Phase 2). At each
/// non-tail <c>Call</c> or tail-call <c>Execute</c> site the IL
/// compiler now checks whether the callee is in the supplied
/// <c>calleeMap</c> and matches <c>IsLeafPredicate</c> (single clause,
/// head match + proceed, no internal Call / Execute). If so, the
/// callee's body opcodes are emitted directly into the caller's IL
/// stream instead of going through the <c>IlCallHelper.Run</c> /
/// <c>IlExecuteHelper.Resolve</c> thunks.
///
/// <para>The chunk is observably a performance optimisation: the
/// caller's IL never touches the bytecode interpreter for an inlined
/// leaf call. These tests pin the observable end-to-end behaviour
/// (same answers as the non-inlined fallback) plus the boundary
/// cases that should <em>not</em> inline (non-leaf callees, callees
/// with internal calls, multi-clause callees).</para>
/// </summary>
public class Chunk69Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void InlinedTailCall_LeafFact_StillCorrect()
    {
        // foo :- q. where q is a leaf fact. Execute opcode in foo's
        // body inlines q's empty head match + proceed.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public foo/0.\n" +
            ":- public q/0.\n" +
            "q.\n" +
            "foo :- q.\n");
        Assert.True(engine.Query("foo.").Success);
        Assert.True(engine.IlPromotion.IsPromoted(Fid("foo", 0)));
    }

    [Fact]
    public void InlinedNonTailCall_LeafFact_StillCorrect()
    {
        // foo :- q, r. q and r are both leaf facts. Both call sites
        // get inlined (q via Call, r via Execute).
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public foo/0.\n" +
            ":- public q/0.\n" +
            ":- public r/0.\n" +
            "q. r.\n" +
            "foo :- q, r.\n");
        Assert.True(engine.Query("foo.").Success);
        Assert.True(engine.IlPromotion.IsPromoted(Fid("foo", 0)));
    }

    [Fact]
    public void InlinedLeafWithHeadMatch_GetsRightArg()
    {
        // foo(X) :- check(X). where check(ok) is a single-clause leaf
        // with a head match. The inlined head match runs against the
        // caller's X[0].
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public check/1.\n" +
            "check(ok).\n" +
            "foo(X) :- check(X).\n");
        Assert.True(engine.Query("foo(ok).").Success);
        Assert.False(engine.Query("foo(nope).").Success);
        Assert.True(engine.IlPromotion.IsPromoted(Fid("foo", 1)));
    }

    [Fact]
    public void InlinedLeaf_FailurePropagatesToCallerFail()
    {
        // foo(X) :- check(X), r. check fails for non-ok; the inlined
        // head match's get_atom branches to the caller's failLabel,
        // and foo returns false.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public check/1.\n" +
            ":- public r/0.\n" +
            "check(ok). r.\n" +
            "foo(X) :- check(X), r.\n");
        Assert.True(engine.Query("foo(ok).").Success);
        Assert.False(engine.Query("foo(not_ok).").Success);
    }

    [Fact]
    public void InlinedLeaf_MatchesTier0Exactly()
    {
        // Same program twice: Tier 0 (no IL promotion) vs Tier 1 (IL
        // with inlining). Solution sets must match.
        var src =
            ":- public outer/1.\n" +
            ":- public inner/1.\n" +
            "inner(a). inner(b). inner(c).\n" +
            "outer(X) :- inner(X).\n";

        var tier0 = new PrologEngine();
        tier0.ConsultString(src);
        var sols0 = tier0.QueryAll("outer(X).").Select(s => s["X"]).ToList();

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(src);
        tier1.Query("outer(a).");   // warm + promote
        var sols1 = tier1.QueryAll("outer(X).").Select(s => s["X"]).ToList();

        Assert.Equal(sols0, sols1);
    }

    [Fact]
    public void NonLeafCallee_NotInlined_StillWorksViaSubcall()
    {
        // foo :- m. where m :- a, b. — m has a non-tail Call inside,
        // so it's NOT a leaf. The caller's Call must NOT inline; it
        // falls back to the IlCallHelper thunk so m's choice points
        // and continuation get the standard sub-call treatment.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public foo/0.\n" +
            ":- public m/0.\n" +
            ":- public a/0.\n" +
            ":- public b/0.\n" +
            "a. b.\n" +
            "m :- a, b.\n" +
            "foo :- m.\n");
        Assert.True(engine.Query("foo.").Success);
    }

    [Fact]
    public void MultiClauseCallee_NotInlined_BacktracksCorrectly()
    {
        // foo(X) :- color(X). where color has multiple clauses.
        // Multi-clause callee can't be inlined as a leaf; chunk 66
        // meta-CP machinery handles the backtracking instead.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public foo/1.\n" +
            ":- public color/1.\n" +
            "color(red). color(green). color(blue).\n" +
            "foo(X) :- color(X).\n");
        var sols = engine.QueryAll("foo(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { new AtomTerm("red"), new AtomTerm("green"), new AtomTerm("blue") }, sols);
    }

    [Fact]
    public void InlinedTailCall_WithCompoundHeadInCallee_StillCorrect()
    {
        // wrap(X) :- check(X). check([7]) is a leaf with a compound
        // head arg. The inlined head match emits get_list + unify_*
        // opcodes against the caller's X[0].
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public wrap/1.\n" +
            ":- public check/1.\n" +
            "check([7]).\n" +
            "wrap(X) :- check(X).\n");
        Assert.True(engine.Query("wrap([7]).").Success);
        Assert.False(engine.Query("wrap([8]).").Success);
        Assert.False(engine.Query("wrap(7).").Success);
    }

    [Fact]
    public void ChainOfThreeLeafCalls_AllInlined()
    {
        // chain :- a, b, c. — three non-tail Calls plus a tail call
        // (the last one is Execute). All three callees are leaves;
        // all three get inlined.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public chain/0.\n" +
            ":- public a/0.\n" +
            ":- public b/0.\n" +
            ":- public c/0.\n" +
            "a. b. c.\n" +
            "chain :- a, b, c.\n");
        Assert.True(engine.Query("chain.").Success);
        Assert.True(engine.IlPromotion.IsPromoted(Fid("chain", 0)));
    }

    [Fact]
    public void InlinedLeaf_BindsVarThroughHeadMatch()
    {
        // outer(X) :- inner(X). where inner(Y) :- Y = found. The
        // inlined head match's get_variable_x picks up the caller's
        // X[0] and the (implicit) head structure of inner means Y is
        // bound to whatever the caller passes in.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public outer/1.\n" +
            ":- public inner/1.\n" +
            "inner(found).\n" +
            "outer(X) :- inner(X).\n");
        var sol = engine.Query("outer(Y).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("found"), sol["Y"]);
    }
}

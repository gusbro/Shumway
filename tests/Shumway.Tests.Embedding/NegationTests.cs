using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class NegationTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- Basic semantics ----------

    [Fact]
    public void Neg_GoalFails_NegationSucceeds()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(a).");
        // \+ p(b) — p(b) fails, \+ succeeds.
        Assert.True(engine.Query("\\+ p(b).").Success);
    }

    [Fact]
    public void Neg_GoalSucceeds_NegationFails()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(a).");
        Assert.False(engine.Query("\\+ p(a).").Success);
    }

    [Fact]
    public void Neg_NotSynonym_BehavesSame()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(a).");
        Assert.True(engine.Query("not(p(b)).").Success);
        Assert.False(engine.Query("not(p(a)).").Success);
    }

    // ---------- fail / true ----------

    [Fact]
    public void Fail_Always()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("fail.").Success);
    }

    [Fact]
    public void True_Always()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("true.").Success);
    }

    [Fact]
    public void Neg_Fail_Succeeds_BecauseFailFails()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("\\+ fail.").Success);
    }

    [Fact]
    public void Neg_True_Fails_BecauseTrueSucceeds()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("\\+ true.").Success);
    }

    // ---------- Composition with other goals ----------

    [Fact]
    public void Neg_InsideConjunction_PropagatesCorrectly()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).\ncolour(green).\ncolour(blue).\n");
        // colour(X), \+ colour(other_X)? — pick a colour, succeed.
        // The negation is over a single colour atom that's not defined.
        var sol = engine.Query("colour(X), \\+ colour(orange).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("red"), sol["X"]);
    }

    [Fact]
    public void Neg_FailureRejectsEntireBody()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(a).\np(b).\nq(b).\n");
        // p(X), \+ q(X) — X must be in p but not in q.
        var solutions = engine.QueryAll("p(X), \\+ q(X).")
            .Select(s => s["X"]).ToList();
        Assert.Single(solutions);
        Assert.Equal(Atom("a"), solutions[0]);
    }

    [Fact]
    public void Neg_DoesNotBindVariables()
    {
        // \+ p(X) should not bind X even if p(X) succeeds for some X.
        // Standard ISO semantics: negation has no side effects on bindings.
        var engine = new PrologEngine();
        engine.ConsultString("p(a). p(b).");
        // \+ \+ p(X) — double-negation. True iff there exists some X with p(X).
        // X should remain unbound.
        var sol = engine.Query("\\+ \\+ p(X).");
        Assert.True(sol.Success);
        // X is still a fresh variable (rendered with _G prefix).
        Assert.True(sol["X"] is VarTerm v && v.Name.StartsWith("_G"));
    }

    // ---------- In rule bodies ----------

    [Fact]
    public void Neg_InRuleBody_ConsultedSource()
    {
        // unique_member(X, L) :- member(X, L), \+ member(X, []).
        // (Just exercises \+ in a consulted clause; the inner condition is always false.)
        var engine = new PrologEngine();
        engine.ConsultString(
            "member(X, [X|_]).\n" +
            "member(X, [_|T]) :- member(X, T).\n" +
            "non_empty_member(X, L) :- member(X, L), \\+ L = [].\n");
        var sol = engine.Query("non_empty_member(X, [a, b]).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("a"), sol["X"]);
    }

    [Fact]
    public void Neg_RuleWithMultipleNegations()
    {
        // p(X) :- \+ q(X), \+ r(X).
        // q(a). r(b).
        // ?- p(X). — but X is unbound, so q(X) and r(X) are non-deterministic.
        // \+ q(X) means "no X makes q succeed" — which fails since q(a) exists.
        // Hmm — \+ q(X) with unbound X tries q(X), which succeeds (binds X=a),
        // so \+ q(X) fails. So p(X) fails.
        var engine = new PrologEngine();
        engine.ConsultString(
            "q(a).\nr(b).\n" +
            "p(X) :- \\+ q(X), \\+ r(X).\n");
        Assert.False(engine.Query("p(X).").Success);
    }

    [Fact]
    public void Neg_OnGroundCheck_TypicalUsage()
    {
        // safe_div(X, Y, R) :- \+ Y = 0, R is X / Y.
        var engine = new PrologEngine();
        engine.ConsultString("safe_div(X, Y, R) :- \\+ Y = 0, R is X / Y.\n");
        var sol = engine.Query("safe_div(10, 4, R).");
        Assert.True(sol.Success);
        // 10 / 4 = 2.5 (float).
        Assert.True(sol["R"] is FloatTerm);
    }

    [Fact]
    public void Neg_DivisionByZero_BlockedByNegation()
    {
        var engine = new PrologEngine();
        engine.ConsultString("safe_div(X, Y, R) :- \\+ Y = 0, R is X / Y.\n");
        Assert.False(engine.Query("safe_div(10, 0, R).").Success);
    }
}

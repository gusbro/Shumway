using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class BuiltinsTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Cmp(string f, params Term[] args) => new CompoundTerm(f, args);

    // ---------- =/2 ----------

    [Fact]
    public void Eq_BindsVariableToAtom()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = foo.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("foo"), sol["X"]);
    }

    [Fact]
    public void Eq_BindsVariableToCompound()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = foo(a, b).");
        Assert.True(sol.Success);
        Assert.Equal(Cmp("foo", Atom("a"), Atom("b")), sol["X"]);
    }

    [Fact]
    public void Eq_UnifiesTwoVariables()
    {
        // X = Y, X = bar  →  Y bound to bar too.
        var engine = new PrologEngine();
        var sol = engine.Query("X = Y, X = bar.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("bar"), sol["X"]);
        Assert.Equal(Atom("bar"), sol["Y"]);
    }

    [Fact]
    public void Eq_MismatchingAtoms_Fail()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo = bar.");
        Assert.False(sol.Success);
    }

    [Fact]
    public void Eq_MismatchingCompoundFunctor_Fails()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo(a) = bar(a).");
        Assert.False(sol.Success);
    }

    [Fact]
    public void Eq_AcceptsNestedCompoundUnification()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo(X, bar(Y)) = foo(1, bar(2)).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["X"]);
        Assert.Equal(Int(2), sol["Y"]);
    }

    // ---------- \=/2 ----------

    [Fact]
    public void NotUnifiable_DifferentAtoms_Succeeds()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo \\= bar.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void NotUnifiable_SameAtom_Fails()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo \\= foo.");
        Assert.False(sol.Success);
    }

    [Fact]
    public void NotUnifiable_LeavesNoBindingsBehindOnTrial()
    {
        // X \= Y succeeds (they're both unbound — wait, actually X \= Y of two
        // distinct unbound vars CAN be unified, so the test should fail).
        // Reformulate: X = 1, X \= 2 succeeds without leaving bindings beyond X = 1.
        var engine = new PrologEngine();
        var sol = engine.Query("X = 1, X \\= 2.");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["X"]);
    }

    [Fact]
    public void NotUnifiable_TwoUnboundVars_Fails()
    {
        // Two unbound variables are unifiable (the unify binds them to each
        // other), so \= reports false. Standard Prolog behaviour.
        var engine = new PrologEngine();
        var sol = engine.Query("X \\= Y.");
        Assert.False(sol.Success);
    }

    // ---------- ==/2 ----------

    [Fact]
    public void StructEq_IdenticalAtoms_Succeeds()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo == foo.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void StructEq_DifferentAtoms_Fails()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo == bar.");
        Assert.False(sol.Success);
    }

    [Fact]
    public void StructEq_UnboundVariableAgainstAtom_Fails()
    {
        // Unlike =/2, ==/2 does NOT bind. X (unbound) is not structurally
        // identical to atom foo.
        var engine = new PrologEngine();
        var sol = engine.Query("X == foo.");
        Assert.False(sol.Success);
    }

    [Fact]
    public void StructEq_TwoUnboundReferringToSameCell_Succeeds()
    {
        // X = Y first aliases them. Then X == Y compares the dereferenced
        // identities, which point at the same heap cell.
        var engine = new PrologEngine();
        var sol = engine.Query("X = Y, X == Y.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void StructEq_TwoDistinctUnbounds_Fails()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X == Y.");
        Assert.False(sol.Success);
    }

    [Fact]
    public void StructEq_CompoundIdentical_Succeeds()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo(a, b) == foo(a, b).");
        Assert.True(sol.Success);
    }

    // ---------- \==/2 ----------

    [Fact]
    public void StructNotEq_DifferentAtoms_Succeeds()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo \\== bar.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void StructNotEq_SameAtom_Fails()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("foo \\== foo.");
        Assert.False(sol.Success);
    }

    // ---------- Builtins in clause bodies ----------

    [Fact]
    public void Builtin_InClauseBody_BindsAndContinues()
    {
        // p(X) :- X = 42.    ?- p(N).    → N = 42.
        var engine = new PrologEngine();
        engine.ConsultString("p(X) :- X = 42.");
        var sol = engine.Query("p(N).");
        Assert.True(sol.Success);
        Assert.Equal(Int(42), sol["N"]);
    }

    [Fact]
    public void Builtin_AsLastGoalInRule_RoundTripsCleanly()
    {
        // p(X, Y) :- X = a, Y = b.   ?- p(A, B).
        var engine = new PrologEngine();
        engine.ConsultString("p(X, Y) :- X = a, Y = b.");
        var sol = engine.Query("p(A, B).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("a"), sol["A"]);
        Assert.Equal(Atom("b"), sol["B"]);
    }

    [Fact]
    public void Builtin_FailingMidBody_TriggersBacktrack()
    {
        // p(a). p(b).  pick(X) :- p(X), X == b.   ?- pick(R).
        // Clause 1 of p binds X to a; the == fails; backtrack picks b.
        var engine = new PrologEngine();
        engine.ConsultString(
            "p(a).\np(b).\n" +
            "pick(X) :- p(X), X == b.\n");
        var sol = engine.Query("pick(R).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("b"), sol["R"]);
    }
}

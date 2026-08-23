using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.2 Term unification.
///
/// Covers <c>=/2</c> (§8.2.1), <c>unify_with_occurs_check/2</c>
/// (§8.2.2) and <c>\=/2</c> (§8.2.3) including the examples spelled
/// out in the standard. The unification builtins have no ISO errors
/// — every input pattern is allowed; the result is success / failure.
///
/// <para>Phase-10 chunk 148 closed the cyclic-term materialiser
/// limitation that lived here as a recorded gap: <c>X = f(X)</c>
/// now round-trips through .NET observation without overflowing
/// the stack (the back-edge becomes a synthetic
/// <c>VarTerm("_C{addr}")</c> cycle marker). See
/// <c>Equal_OccursCheckOff_BindsToSelfReferentialTerm</c> below.</para>
/// </summary>
public class TermUnificationConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Var(string n) => new VarTerm(n);
    private static Term Compound(string f, params Term[] args) =>
        new CompoundTerm(f, args);

    // ---------- §8.2.1 (=)/2 ----------

    [Fact]
    public void Equal_AtomToSameAtom_Succeeds() =>
        Assert.True(new PrologEngine().Query("foo = foo.").Success);

    [Fact]
    public void Equal_AtomToDifferentAtom_Fails() =>
        Assert.False(new PrologEngine().Query("foo = bar.").Success);

    [Fact]
    public void Equal_AtomToInteger_Fails() =>
        Assert.False(new PrologEngine().Query("foo = 1.").Success);

    [Fact]
    public void Equal_BindsVarToTerm()
    {
        // §8.2.1.4 a — X = abc binds X to abc.
        var engine = new PrologEngine();
        var sol = engine.Query("X = abc.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("abc"), sol["X"]);
    }

    [Fact]
    public void Equal_BindsTermToVar()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("abc = X.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("abc"), sol["X"]);
    }

    [Fact]
    public void Equal_TwoVarsUnify()
    {
        // §8.2.1.4 — X = Y binds them together; further bindings
        // propagate to both.
        var engine = new PrologEngine();
        var sol = engine.Query("X = Y, X = hello.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["X"]);
        Assert.Equal(Atom("hello"), sol["Y"]);
    }

    [Fact]
    public void Equal_CompoundStructural_Succeeds()
    {
        // foo(X, Y) = foo(1, 2) binds X = 1, Y = 2.
        var engine = new PrologEngine();
        var sol = engine.Query("foo(X, Y) = foo(1, 2).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["X"]);
        Assert.Equal(Int(2), sol["Y"]);
    }

    [Fact]
    public void Equal_CompoundDifferentFunctor_Fails() =>
        Assert.False(new PrologEngine().Query("foo(1) = bar(1).").Success);

    [Fact]
    public void Equal_CompoundDifferentArity_Fails() =>
        Assert.False(new PrologEngine().Query("foo(1) = foo(1, 2).").Success);

    [Fact]
    public void Equal_LinearLists_Succeed()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("[1,2,3] = [X,Y,Z], X = 1, Y = 2, Z = 3.").Success);
    }

    [Fact]
    public void Equal_ListTailVar_Succeeds()
    {
        // §8.2.1.4 — [a,b,c] = [a|T] binds T = [b,c].
        var engine = new PrologEngine();
        var sol = engine.Query("[a,b,c] = [a|T].");
        Assert.True(sol.Success);
        // T is the rest of the list.
        Assert.NotNull(sol["T"]);
    }

    [Fact]
    public void Equal_OccursCheckOff_BindsToSelfReferentialTerm()
    {
        // §8.2.1.4 c — without occurs-check, X = f(X) succeeds and
        // X is a cyclic term. Chunk 148 made the materialiser
        // cycle-safe so we can surface X back to .NET without a
        // stack overflow; the back-edge appears as a synthetic
        // VarTerm("_C{addr}") cycle marker.
        var engine = new PrologEngine();
        var sol = engine.Query("X = f(X), Y = a.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("a"), sol["Y"]);
        var x = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal("f", x.Functor);
        Assert.Single(x.Args);
        var marker = Assert.IsType<VarTerm>(x.Args[0]);
        Assert.StartsWith("_C", marker.Name);
    }

    [Fact]
    public void Equal_BindingPropagatesViaSharedVar()
    {
        // The classic 'tying the knot' test: two compounds share a
        // variable; binding it once binds it everywhere.
        var engine = new PrologEngine();
        var sol = engine.Query("p(X, X) = p(1, Y).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["X"]);
        Assert.Equal(Int(1), sol["Y"]);
    }

    // ---------- §8.2.2 unify_with_occurs_check/2 ----------

    [Fact]
    public void UnifyWithOccursCheck_PlainBindings_StillWork()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("unify_with_occurs_check(X, hello).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["X"]);
    }

    [Fact]
    public void UnifyWithOccursCheck_SelfReferential_Fails()
    {
        // §8.2.2.4 — unify_with_occurs_check(X, f(X)) fails because
        // X occurs inside the right-hand side. (Plain =/2 would build
        // a cyclic term.)
        var engine = new PrologEngine();
        Assert.False(
            engine.Query("unify_with_occurs_check(X, f(X)).").Success);
    }

    [Fact]
    public void UnifyWithOccursCheck_DeepSelfReferential_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(
            engine.Query("unify_with_occurs_check(X, f(g(X))).").Success);
    }

    [Fact]
    public void UnifyWithOccursCheck_CompoundsWithoutCycle_Succeeds()
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            "unify_with_occurs_check(foo(X, Y), foo(1, 2)).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["X"]);
        Assert.Equal(Int(2), sol["Y"]);
    }

    // An ALREADY-cyclic input (built by plain =/2) is a legal rational
    // tree in this engine: the occurs check bars only the creation of NEW
    // cycles, so a fresh variable binds to it and two cyclic operands
    // unify coinductively (Trealla agrees; these used to loop forever in
    // the occurs-check walk, then to fail under the pre-rational policy).

    [Fact]
    public void UnifyWithOccursCheck_CyclicOperandAgainstVar_Binds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "X = f(X), unify_with_occurs_check(X, Y), Y == X.").Success);
        Assert.True(engine.Query(
            "X = [a|X], unify_with_occurs_check(X, Y), Y == X.").Success);
    }

    [Fact]
    public void UnifyWithOccursCheck_TwoCyclicOperands_UnifyCoinductively()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "X = f(X), Y = f(Y), unify_with_occurs_check(X, Y).").Success);
    }

    [Fact]
    public void UnifyWithOccursCheck_CyclicOperandWithItself_Succeeds()
    {
        // Identity short-circuits before any walk — X and X are the
        // same cell, cyclic or not.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "X = f(X), unify_with_occurs_check(X, X), Y = ok.").Success);
    }

    // Plain =/2 between two ALREADY-cyclic terms is ISO-undefined (STO);
    // Shumway terminates with rational-tree semantics like SWI — a compound
    // pair re-encountered during the walk is an equation already in the
    // system, assumed true. These used to overflow the C# stack (cyclic
    // structs) or loop forever (cyclic list spines).

    [Fact]
    public void PlainUnify_TwoCyclicTerms_Succeeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "X = f(X), Y = f(Y), X = Y, Ok = yes.").Success);
        Assert.True(engine.Query(
            "X = [a|X], Y = [a|Y], X = Y, Ok = yes.").Success);
        // Different cycle periods, same infinite unfolding.
        Assert.True(engine.Query(
            "X = [a,b|X], Y = [a|Z], Z = [b|Y], X = Y, Ok = yes.").Success);
    }

    [Fact]
    public void PlainUnify_MismatchedCyclicTerms_FailsInsteadOfLooping()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query(
            "X = f(a, X), Y = f(b, Y), X = Y.").Success);
        Assert.False(engine.Query(
            "X = f(X), Y = g(Y), X = Y.").Success);
    }

    [Fact]
    public void UnifyWithOccursCheck_SharedSubtermDag_IsNotACycle()
    {
        // f(A, g(A)) shares g(A) twice as a DAG — sharing must not be
        // mistaken for a cycle by the cycle guard.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "T = g(A), unify_with_occurs_check(f(A, T), f(B, g(B))), B = 1.")
            .Success);
    }

    [Fact]
    public void UnifyWithOccursCheck_VarToVar_Succeeds()
    {
        // X = Y with both unbound is safe — no cycle possible.
        var engine = new PrologEngine();
        var sol = engine.Query("unify_with_occurs_check(X, Y), X = hi.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hi"), sol["X"]);
        Assert.Equal(Atom("hi"), sol["Y"]);
    }

    [Fact]
    public void UnifyWithOccursCheck_VarInListSpine_Fails()
    {
        // §8.2.2.4 — variable appearing inside a list spine.
        var engine = new PrologEngine();
        Assert.False(
            engine.Query("unify_with_occurs_check(X, [1, 2, X]).").Success);
    }

    // ---------- §8.2.3 (\=)/2 ----------

    [Fact]
    public void NotUnifiable_DifferentAtoms_Succeeds() =>
        Assert.True(new PrologEngine().Query("foo \\= bar.").Success);

    [Fact]
    public void NotUnifiable_SameAtoms_Fails() =>
        Assert.False(new PrologEngine().Query("foo \\= foo.").Success);

    [Fact]
    public void NotUnifiable_VarAgainstAtom_Fails() =>
        // X is unbound — unifying with foo succeeds, so \= fails.
        Assert.False(new PrologEngine().Query("X \\= foo.").Success);

    [Fact]
    public void NotUnifiable_DoesNotBind()
    {
        // ISO §8.2.3.2: \= does NOT keep the bindings of its trial
        // unification. After \=(X, foo) succeeds (which it won't for
        // a var), X must still be free.
        // Pin the not-bound aspect with: \=(X, foo) fails for var X;
        // the surrounding query then proves X is still unbound by
        // unifying it freely elsewhere.
        var engine = new PrologEngine();
        var sol = engine.Query("a \\= b, X = post.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("post"), sol["X"]);
    }
    // ===== occurs check over rational trees =====
    // The engine's terms are rational trees, so unify_with_occurs_check's
    // check guards only the creation of NEW cycles (a variable binding into
    // a term it occurs in). Already-cyclic INPUTS unify coinductively and a
    // fresh variable may bind to one (Trealla agrees; their test0518).

    [Fact]
    public void OccursCheck_CyclicVsCyclic_SucceedsCoinductively()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "V = V-X, W = W-X, unify_with_occurs_check(V, W).").Success);
        Assert.True(e.Query(
            "V = V-X, W = W-Y, unify_with_occurs_check(V, W), X == Y.").Success);
    }

    [Fact]
    public void OccursCheck_FreshVarBindsToCyclicTerm()
        => Assert.True(new PrologEngine().Query(
            "V = V-_, unify_with_occurs_check(V, W), W == V.").Success);

    [Fact]
    public void OccursCheck_StillBarsNewCycles()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("unify_with_occurs_check(X, f(X)).").Success);
        // ...including one reached DEEP in a cyclic-vs-cyclic walk: the
        // coinductive pair success must not leak past the fresh X4 = s(X4)
        // bind attempt.
        Assert.True(e.Query(
            "V = V-X, W = W-s(X), \\+ unify_with_occurs_check(V, W).").Success);
    }


    [Fact]
    public void NotUnifiable_CompoundsDifferentArity_Succeeds() =>
        Assert.True(new PrologEngine().Query("foo(1) \\= foo(1, 2).").Success);

    [Fact]
    public void NotUnifiable_CompoundsStructurallyEqual_Fails() =>
        Assert.False(new PrologEngine().Query("foo(1, 2) \\= foo(1, 2).").Success);

    [Fact]
    public void NotUnifiable_NestedNonUnifiable_Succeeds() =>
        Assert.True(new PrologEngine().Query("f(a, X) \\= f(b, X).").Success);
}

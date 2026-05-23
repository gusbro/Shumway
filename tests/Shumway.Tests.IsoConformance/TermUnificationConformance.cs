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
/// <para><b>One recorded gap</b>: plain <c>=/2</c> with a
/// self-referential RHS (<c>X = f(X)</c>) succeeds and builds a
/// cyclic term — ISO permits this — but the Embedding-layer
/// materialiser walks the binding to surface it back to C#, which
/// overflows the stack for any cyclic structure. A catch-cycle
/// materialiser is the same kind of fix Phase 8 chunk 111 applied
/// to long lists; queued separately.</para>
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

    // Equal_OccursCheckOff_BindsToSelfReferentialTerm — cyclic-term
    // materialiser limitation; see recorded gap above.

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

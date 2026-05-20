using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 77 — attributed variables (Phase 4 foundation). An attributed
/// variable is an unbound variable that additionally carries a set of
/// (module, value) attribute pairs. <c>put_attr/3</c>, <c>get_attr/3</c>
/// and <c>del_attr/2</c> attach, read and remove them; <c>attvar/1</c>
/// tests for one; and <c>var/1</c> / <c>nonvar/1</c> keep treating an
/// attributed variable as a variable.
///
/// <para>This chunk is the hook-less foundation: unifying an attributed
/// variable still binds it (and merges attributes when two attributed
/// variables meet), but no <c>attr_unify_hook</c> fires — that is
/// chunk 78. Every attribute mutation is trailed, so attributes revert
/// cleanly on backtracking.</para>
/// </summary>
public class Chunk77Tests
{
    private static PrologEngine NewEngine() => new();

    // ---- put_attr / get_attr round-trip --------------------------------

    [Fact]
    public void PutThenGet_RoundTripsTheAttribute()
    {
        var engine = NewEngine();
        var sol = engine.Query("put_attr(X, m, 42), get_attr(X, m, V).");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm(42), sol["V"]);
    }

    [Fact]
    public void GetAttr_AbsentModule_Fails()
    {
        var engine = NewEngine();
        Assert.False(engine.Query("put_attr(X, m, 1), get_attr(X, other, _).").Success);
    }

    [Fact]
    public void GetAttr_OnPlainVariable_Fails()
    {
        // A variable with no put_attr is not attributed — get_attr fails.
        var engine = NewEngine();
        Assert.False(engine.Query("get_attr(_, m, _).").Success);
    }

    [Fact]
    public void PutAttr_OverwritesTheModuleValue()
    {
        var engine = NewEngine();
        var sol = engine.Query("put_attr(X, m, 1), put_attr(X, m, 2), get_attr(X, m, V).");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm(2), sol["V"]);
    }

    // ---- del_attr ------------------------------------------------------

    [Fact]
    public void DelAttr_RemovesTheAttribute()
    {
        var engine = NewEngine();
        Assert.False(engine.Query(
            "put_attr(X, m, 1), del_attr(X, m), get_attr(X, m, _).").Success);
    }

    [Fact]
    public void DelAttr_LeavesOtherModulesIntact()
    {
        var engine = NewEngine();
        var sol = engine.Query(
            "put_attr(X, a, 1), put_attr(X, b, 2), del_attr(X, a), get_attr(X, b, V).");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm(2), sol["V"]);
    }

    [Fact]
    public void DelAttr_OnPlainVariable_StillSucceeds()
    {
        // del_attr is a no-op (but succeeds) when there is nothing to remove.
        var engine = NewEngine();
        Assert.True(engine.Query("del_attr(_, m).").Success);
    }

    // ---- multiple modules ---------------------------------------------

    [Fact]
    public void MultipleModules_CoexistOnOneVariable()
    {
        var engine = NewEngine();
        var sol = engine.Query(
            "put_attr(X, m1, alpha), put_attr(X, m2, beta), " +
            "get_attr(X, m1, P), get_attr(X, m2, Q).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("alpha"), sol["P"]);
        Assert.Equal(new AtomTerm("beta"), sol["Q"]);
    }

    // ---- type tests ----------------------------------------------------

    [Fact]
    public void Attvar_IsRecognisedByAttvarPredicate()
    {
        var engine = NewEngine();
        Assert.True(engine.Query("put_attr(X, m, 1), attvar(X).").Success);
    }

    [Fact]
    public void PlainVariable_IsNotAttvar()
    {
        var engine = NewEngine();
        Assert.False(engine.Query("attvar(_).").Success);
    }

    [Fact]
    public void BoundTerm_IsNotAttvar()
    {
        var engine = NewEngine();
        Assert.False(engine.Query("attvar(foo).").Success);
    }

    [Fact]
    public void Attvar_CountsAsAVariable()
    {
        // var/1 must still hold — an attributed variable has no value.
        var engine = NewEngine();
        Assert.True(engine.Query("put_attr(X, m, 1), var(X).").Success);
        Assert.False(engine.Query("put_attr(X, m, 1), nonvar(X).").Success);
    }

    [Fact]
    public void BoundAttvar_BecomesNonvarAndLosesAttvarStatus()
    {
        var engine = NewEngine();
        Assert.True(engine.Query("put_attr(X, m, 1), X = concrete, nonvar(X).").Success);
        Assert.False(engine.Query("put_attr(X, m, 1), X = concrete, attvar(X).").Success);
    }

    // ---- unification ---------------------------------------------------

    [Fact]
    public void Attvar_UnifiesWithPlainVariable_PreservingAttributes()
    {
        // The plain variable binds to the attributed one; the attributes
        // survive and are reachable through either name.
        var engine = NewEngine();
        var sol = engine.Query("put_attr(X, m, 7), X = Y, get_attr(Y, m, V), attvar(Y).");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm(7), sol["V"]);
    }

    [Fact]
    public void Attvar_UnifiesWithConcreteValue()
    {
        var engine = NewEngine();
        Assert.True(engine.Query("put_attr(X, m, 1), X = hello, X == hello.").Success);
    }

    [Fact]
    public void TwoAttvars_DisjointModules_MergeOnUnification()
    {
        var engine = NewEngine();
        var sol = engine.Query(
            "put_attr(X, m1, one), put_attr(Y, m2, two), X = Y, " +
            "get_attr(X, m1, P), get_attr(X, m2, Q).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("one"), sol["P"]);
        Assert.Equal(new AtomTerm("two"), sol["Q"]);
    }

    [Fact]
    public void TwoAttvars_SameModuleEqualValue_UnifySuccessfully()
    {
        var engine = NewEngine();
        Assert.True(engine.Query(
            "put_attr(X, m, same), put_attr(Y, m, same), X = Y.").Success);
    }

    [Fact]
    public void TwoAttvars_SameModuleConflictingValues_FailUnification()
    {
        // The hookless merge rule: a module both attributed variables
        // carry must hold unifiable values, or the whole unification fails.
        var engine = NewEngine();
        Assert.False(engine.Query(
            "put_attr(X, m, 1), put_attr(Y, m, 2), X = Y.").Success);
    }

    // ---- attributed variable inside a compound -------------------------

    [Fact]
    public void Attvar_SurvivesInsideACompoundTerm()
    {
        // foo(X) is built with X attributed, then matched against foo(A):
        // A must end up sharing X's attribute.
        var engine = NewEngine();
        var sol = engine.Query(
            "put_attr(X, m, deep), T = foo(X), T = foo(A), get_attr(A, m, V).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("deep"), sol["V"]);
    }

    [Fact]
    public void StructuralEquality_HandlesAttvarsInsideCompounds()
    {
        var engine = NewEngine();
        // Same attributed variable in both positions — structurally equal.
        Assert.True(engine.Query("put_attr(X, m, 1), foo(X) == foo(X).").Success);
        // Distinct attributed variables — structurally different.
        Assert.True(engine.Query(
            "put_attr(X, m, 1), put_attr(Y, m, 1), foo(X) \\== foo(Y).").Success);
    }

    [Fact]
    public void Attvar_OrdersAsAVariableInStandardOrder()
    {
        // Variables precede numbers in the ISO standard order of terms;
        // an attributed variable is still a variable.
        var engine = NewEngine();
        Assert.True(engine.Query("put_attr(X, m, 1), X @< 0.").Success);
    }

    // ---- backtracking --------------------------------------------------

    [Fact]
    public void PutAttr_RevertsOnBacktracking()
    {
        // The attribute attached in the failing branch must be gone once
        // execution backtracks out of it.
        var engine = NewEngine();
        var sol = engine.Query(
            "( put_attr(X, m, 1), fail ; true ), " +
            "( get_attr(X, m, _) -> R = present ; R = absent ).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("absent"), sol["R"]);
    }

    [Fact]
    public void PutAttr_ModificationRevertsToPreviousValue()
    {
        // An overwrite inside a failing branch reverts to the value the
        // attribute had before that branch.
        var engine = NewEngine();
        var sol = engine.Query(
            "put_attr(X, m, first), " +
            "( put_attr(X, m, second), fail ; get_attr(X, m, V) ).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("first"), sol["V"]);
    }

    [Fact]
    public void AttributeAttachedBeforeAChoicePoint_SurvivesEveryBranch()
    {
        // The attribute is attached once, then read on each of three
        // backtracking solutions — it must be visible every time.
        var engine = NewEngine();
        var values = engine.QueryAll(
                "put_attr(X, m, kept), member(_, [a,b,c]), get_attr(X, m, V).")
            .Select(s => s["V"])
            .ToList();
        Assert.Equal(
            new Term[] { new AtomTerm("kept"), new AtomTerm("kept"), new AtomTerm("kept") },
            values);
    }

    // ---- error handling ------------------------------------------------

    [Fact]
    public void PutAttr_OnANonVariable_ThrowsTypeError()
    {
        var engine = NewEngine();
        Assert.True(engine.Query(
            "catch(put_attr(concrete, m, 1), error(type_error(var, _), _), true).").Success);
    }
}

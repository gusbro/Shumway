using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Stage 4 of the M:P story — the goal side, audited. The core has
/// long worked (static rewrite + $mqual + the ':'/2 prelude predicate); this
/// pins it and the edges the audit found: a qualified goal whose module slot
/// is unbound or a non-atom used to LOOP FOREVER — the meta-dispatch
/// declined to unwrap it and dispatched ':'/2 as a predicate, whose prelude
/// clause is call(M:G) all over again. Now they are the ISO errors, shapes
/// pinned against SWI: instantiation_error / type_error(atom, Culprit).</summary>
public sealed class QualifiedGoalTests
{
    private const string ModG = """
        :- module(qg, []).
        loc(1).
        loc(2).
        pair(a, 1).
        pair(b, 2).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(ModG);
        return e;
    }

    [Fact]
    public void QualifiedGoals_ResolveEverywhere()
    {
        var e = Engine();
        Assert.True(e.Query("qg:loc(1).").Success);
        Assert.True(e.Query("call(qg:loc(2)).").Success);
        Assert.True(e.Query("call(qg:loc, 1).").Success);
        Assert.True(e.Query("findall(X, qg:loc(X), [1, 2]).").Success);
        Assert.True(e.Query("bagof(X, qg:loc(X), [1, 2]).").Success);
        Assert.True(e.Query("setof(X, qg:loc(X), [1, 2]).").Success);
        Assert.True(e.Query("bagof(X, Y^(qg:pair(X, Y)), [a, b]).").Success);
    }

    [Fact]
    public void NestedQualification_InnermostModuleWins()
    {
        var e = Engine();
        Assert.True(e.Query("user:qg:loc(1).").Success);
        Assert.True(e.Query("call(user:qg:loc(1)).").Success);
    }

    [Fact]
    public void QualifierDistributes_OverControlConstructs()
    {
        var e = Engine();
        Assert.True(e.Query("call(qg:(loc(1), loc(2))).").Success);
        Assert.True(e.Query("call(qg:(loc(9) ; loc(2))).").Success);
        Assert.True(e.Query("call(qg:(loc(1) -> loc(2) ; fail)).").Success);
        Assert.True(e.Query("call(qg:( \\+ loc(9) )).").Success);
    }

    [Fact]
    public void UnboundModule_IsAnInstantiationError()
    {
        var e = Engine();
        // Used to loop forever through the ':'/2 prelude clause.
        Assert.True(e.Query(
            "catch(call(M:foo), error(instantiation_error, _), true).").Success);
    }

    [Fact]
    public void NonAtomModule_IsATypeError_WithTheCulprit()
    {
        var e = Engine();
        // Used to loop forever. SWI shape: type_error(atom, 1).
        Assert.True(e.Query(
            "catch(call(1:foo), error(type_error(atom, 1), _), true).").Success);
        Assert.True(e.Query(
            "catch(1:foo, error(type_error(atom, 1), _), true).").Success);
        // Nested with a bad INNER module: the bad slot is the one reported.
        Assert.True(e.Query(
            "catch(call(qg:2:foo), error(type_error(atom, 2), _), true).").Success);
    }
}

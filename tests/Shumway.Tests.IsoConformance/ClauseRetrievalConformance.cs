using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.8 Clause retrieval and information.
///
/// Covers <c>clause/2</c> (§8.8.1) and <c>current_predicate/1</c>
/// (§8.8.2): inspecting the database.
///
/// <para>Shumway's prelude wraps these around the <c>'$all_clauses_of'/2</c>
/// and <c>'$all_predicate_indicators'/1</c> introspection builtins
/// (chunk 47) and uses <c>member/2</c> over the result to enumerate
/// solutions on backtracking.</para>
/// </summary>
public class ClauseRetrievalConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Compound(string f, params Term[] args) =>
        new CompoundTerm(f, args);

    // ---------- §8.8.1 clause/2 ----------

    [Fact]
    public void Clause_StaticFact_RetrievesBodyTrue()
    {
        // For a fact `p(1).`, clause(p(1), B) should give B = true.
        var e = new PrologEngine();
        e.ConsultString("p(1). p(2).");
        var sol = e.Query("clause(p(1), B).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("true"), sol["B"]);
    }

    [Fact]
    public void Clause_StaticRule_RetrievesBody()
    {
        var e = new PrologEngine();
        e.ConsultString("p(X) :- q(X), r(X). q(1). r(1).");
        var sol = e.Query("clause(p(X), Body).");
        Assert.True(sol.Success);
        // Body unifies with q(X), r(X) — Body has structure ','(...).
        var body = Assert.IsType<CompoundTerm>(sol["Body"]);
        Assert.Equal(",", body.Functor);
    }

    [Fact]
    public void Clause_EnumeratesEveryMatch()
    {
        // Backtracking visits each clause head matching the pattern.
        var e = new PrologEngine();
        e.ConsultString("p(1). p(2). p(3).");
        var xs = e.QueryAll("clause(p(X), _).")
            .Select(s => (s["X"] as IntTerm)!.Value)
            .ToList();
        Assert.Equal(new long[] { 1, 2, 3 }, xs);
    }

    [Fact]
    public void Clause_DynamicPredicate_SeesAssertedClauses()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query("assertz(d(7)), clause(d(7), true).").Success);
    }

    [Fact]
    public void Clause_NoMatch_Fails()
    {
        var e = new PrologEngine();
        e.ConsultString("p(1).");
        Assert.False(e.Query("clause(p(99), _).").Success);
    }

    // ---------- §8.8.2 current_predicate/1 ----------

    [Fact]
    public void CurrentPredicate_FindsUserDefined()
    {
        var e = new PrologEngine();
        e.ConsultString("foo(1). bar(_, _).");
        Assert.True(e.Query("current_predicate(foo/1).").Success);
        Assert.True(e.Query("current_predicate(bar/2).").Success);
    }

    [Fact]
    public void CurrentPredicate_NotDefined_Fails()
    {
        var e = new PrologEngine();
        // A predicate the user never defined.
        Assert.False(e.Query("current_predicate(nope/3).").Success);
    }

    [Fact]
    public void CurrentPredicate_VarIndicator_EnumeratesUserAndBuiltins()
    {
        // ISO: with a var indicator, the call enumerates every
        // defined predicate as a Name/Arity term. Pin the test
        // structurally so a renderer change doesn't break it.
        var e = new PrologEngine();
        e.ConsultString("foo(1).");
        var indicators = e.QueryAll("current_predicate(I).")
            .Select(s => s["I"])
            .OfType<CompoundTerm>()
            .Where(c => c.Functor == "/" && c.Args.Length == 2)
            .Select(c => (Name: (c.Args[0] as AtomTerm)?.Name,
                          Arity: (c.Args[1] as IntTerm)?.Value))
            .ToList();

        // Built-ins should be enumerated.
        Assert.Contains(indicators, p => p.Name == "is" && p.Arity == 2);
        // User-defined predicates should be enumerated too.
        Assert.Contains(indicators, p => p.Name == "foo" && p.Arity == 1);
    }

    [Fact]
    public void CurrentPredicate_BadIndicatorShape_RaisesTypeError()
    {
        // §8.8.2.3 — neither Name/Arity nor var ⇒ type_error.
        // The prelude's '$check_predicate_indicator' raises a
        // user-facing error/2 — a hand-built throw, not via the
        // PrologRuntimeException path, so it surfaces as a
        // ShumwayPrologException.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(current_predicate(foo), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("predicate_indicator"), sol["T"]);
    }

    [Fact]
    public void CurrentPredicate_BuiltinsAreVisible()
    {
        // Shumway exposes builtins through current_predicate too —
        // chunk 47's '$all_predicate_indicators' merges them in.
        var e = new PrologEngine();
        // 'is' is a core arithmetic builtin.
        Assert.True(e.Query("current_predicate(is/2).").Success);
    }
}

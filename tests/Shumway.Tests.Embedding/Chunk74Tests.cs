using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 74 — specialized code generation per mode (Phase 3). The
/// first mode-aware code-gen pass: when every declared mode of a
/// predicate is deterministic (det / semidet), the
/// <c>ModeSpecializationTransform</c> appends an implicit trailing
/// cut to each of its clauses. The cut commits to the first clause
/// whose head and body both succeed and discards every choice point
/// created since the predicate was entered, so a predicate the user
/// promised was deterministic actually leaves no dangling choice
/// point.
///
/// <para>The <c>:- mode ... is det/semidet</c> declaration is a
/// contract: these tests confirm the transform honours it (the
/// predicate becomes single-solution) and, just as importantly,
/// leaves multi / nondet and undeclared predicates with full
/// backtracking.</para>
/// </summary>
public class Chunk74Tests
{
    [Fact]
    public void DetPredicate_YieldsFirstSolutionOnly()
    {
        // pick/2 genuinely has two solutions for pick(a, X), but the
        // det declaration is a contract: the trailing cut commits to
        // the first.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode pick(+, -) is det.
            pick(a, 1).
            pick(a, 2).
            pick(b, 3).
            """);
        var sols = engine.QueryAll("pick(a, X).").Select(s => s["X"]).ToList();
        Assert.Single(sols);
        Assert.Equal(new IntTerm(1), sols[0]);
    }

    [Fact]
    public void SemidetPredicate_YieldsFirstSolutionOnly()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode opt(+, -) is semidet.
            opt(x, first).
            opt(x, second).
            """);
        Assert.Single(engine.QueryAll("opt(x, R)."));
    }

    [Fact]
    public void NondetPredicate_KeepsAllSolutions()
    {
        // Declared nondet → no specialization → full backtracking.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode gen(+, -) is nondet.
            gen(a, 1).
            gen(a, 2).
            gen(a, 3).
            """);
        Assert.Equal(3, engine.QueryAll("gen(a, X).").Count());
    }

    [Fact]
    public void MultiPredicate_KeepsAllSolutions()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode many(+, -) is multi.
            many(k, 1).
            many(k, 2).
            """);
        Assert.Equal(2, engine.QueryAll("many(k, X).").Count());
    }

    [Fact]
    public void UndeclaredPredicate_KeepsAllSolutions()
    {
        // No :- mode directive at all → no specialization.
        var engine = new PrologEngine();
        engine.ConsultString("""
            plain(a, 1).
            plain(a, 2).
            plain(a, 3).
            """);
        Assert.Equal(3, engine.QueryAll("plain(a, X).").Count());
    }

    [Fact]
    public void MixedModes_NotSpecialized()
    {
        // append/3-style: det in one mode, nondet in another. Because
        // one declared mode is nondet, the cut-append is unsafe and
        // the predicate keeps full backtracking.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode route(+, -) is det.
            :- mode route(-, +) is nondet.
            route(a, 1).
            route(a, 2).
            """);
        // Not specialized → both solutions survive.
        Assert.Equal(2, engine.QueryAll("route(a, X).").Count());
    }

    [Fact]
    public void GenuinelyDetPredicate_CorrectnessPreserved()
    {
        // classify/2's clauses are mutually exclusive by body, so the
        // predicate is genuinely deterministic. The cut-append must
        // not change any answer.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode classify(+, -) is det.
            classify(X, negative) :- X < 0.
            classify(0, zero).
            classify(X, positive) :- X > 0.
            """);
        Assert.Equal(new AtomTerm("negative"), engine.Query("classify(-5, C).")["C"]);
        Assert.Equal(new AtomTerm("zero"), engine.Query("classify(0, C).")["C"]);
        Assert.Equal(new AtomTerm("positive"), engine.Query("classify(7, C).")["C"]);
    }

    [Fact]
    public void DetPredicate_BodyFailureFallsThroughToNextClause()
    {
        // The cut is trailing: it fires only after a clause's body
        // succeeds. If clause 1's body fails, clause 2 is still tried.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode lookup(+, -) is semidet.
            lookup(K, V) :- table(K, V), V > 10.
            lookup(K, V) :- table(K, V).
            :- public table/2.
            table(a, 5).
            table(a, 50).
            """);
        // Clause 1: table(a,5) → 5>10 fails; table(a,50) → 50>10 ok → V=50.
        var sol = engine.Query("lookup(a, V).");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm(50), sol["V"]);
        // semidet → exactly one solution even though clause 2 would
        // also match.
        Assert.Single(engine.QueryAll("lookup(a, V)."));
    }

    [Fact]
    public void DetFact_BecomesDeterministic()
    {
        // Fact-form clauses (no body) of a det predicate also get the
        // implicit cut: H. → H :- !.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode color(-) is det.
            color(red).
            color(green).
            color(blue).
            """);
        Assert.Single(engine.QueryAll("color(X)."));
    }

    [Fact]
    public void DetPredicate_StillSucceedsAndBinds()
    {
        // Sanity: specialization doesn't break the basic success path.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode double(+, -) is det.
            double(X, Y) :- Y is X * 2.
            """);
        Assert.Equal(new IntTerm(14), engine.Query("double(7, R).")["R"]);
    }

    [Fact]
    public void DetPredicate_FailureStillFails()
    {
        // A det predicate that doesn't match still fails cleanly.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode only(+) is semidet.
            only(yes).
            """);
        Assert.True(engine.Query("only(yes).").Success);
        Assert.False(engine.Query("only(no).").Success);
    }

    [Fact]
    public void DynamicPredicate_DetSpecializationApplies()
    {
        // A dynamic predicate declared det also gets the cut-append;
        // the chunk-68 cache caches the specialized compiled form.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- dynamic d/2.
            :- mode d(+, -) is det.
            """);
        engine.Query("assertz(d(k, 1)).");
        engine.Query("assertz(d(k, 2)).");
        Assert.Single(engine.QueryAll("d(k, X)."));
    }

    [Fact]
    public void DetPredicate_CutDoesNotEscapeToCaller()
    {
        // The implicit cut is clause-local: it commits within the det
        // predicate but must not prune the caller's choice points.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- mode classify1(+, -) is det.
            classify1(X, small) :- X < 10.
            classify1(X, big) :- X >= 10.
            :- public num/1.
            num(3).
            num(20).
            num(7).
            :- public describe/2.
            describe(N, C) :- num(N), classify1(N, C).
            """);
        // num/1 backtracks (3 solutions); classify1 is det per call but
        // its cut must not kill num/1's choice points.
        var sols = engine.QueryAll("describe(N, C).").ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal(new IntTerm(3), sols[0]["N"]);
        Assert.Equal(new AtomTerm("small"), sols[0]["C"]);
        Assert.Equal(new IntTerm(20), sols[1]["N"]);
        Assert.Equal(new AtomTerm("big"), sols[1]["C"]);
    }
}

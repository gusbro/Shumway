using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-063: a fact asserted at run time is kept as the image of its
/// head, compiled from it without a term or the clause compiler, and tried by
/// retract/1 and clause/2 against the pattern as cells. What a program sees is
/// what it saw with the clause as a term: the same answers, in the same order,
/// the same listing.</summary>
public sealed class ImageFactTests
{
    private static PrologEngine Engine(int threshold, bool images)
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.Flags.ImageFacts = images;
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(":- dynamic(p/2).");
        return e;
    }

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static string Output(PrologEngine e)
    {
        string text = e.Out!.ToString()!;
        ((StringWriter)e.Out).GetStringBuilder().Clear();
        return text;
    }

    // Every shape a head can take: atoms, small and wide integers, variables
    // repeated across arguments and inside a compound, a float, packed text, an
    // open list; then calls bound and unbound, a loop that makes the predicate
    // hot, clause/2, a listing, and retracts in every form.
    private static readonly string[] Script =
    {
        "assertz(p(a, 1)), assertz(p(b, 2)), asserta(p(z, 0)), assertz(p(X, X)), assertz(p(f(Y), Y)).",
        @"assertz(p(""txt"", 2.5)), assertz(p([1, 2|T], T)), assertz(p(5000000000, [])), assertz(p(g(h(i)), j)).",
        "assertz(p(h(Z, Z), w)).",
        "assertz(p(big, g(1, 2, 3, 4, 5, 6, 7, h(8, 9)))), assertz(p(sh(Q), t(Q, Q, a, b, c, d, e, f))), assertz(p(ho(R), t(S, R, S, a, b, c, d, e))).",
        "p(big, G), G == g(1, 2, 3, 4, 5, 6, 7, h(8, 9)), p(big, g(1, 2, 3, 4, 5, 6, 7, h(8, X))), X == 9, \\+ p(big, g(1, 2, 3, 4, 5, 6, 7, h(9, _))).",
        "p(sh(1), T), T == t(1, 1, a, b, c, d, e, f), p(sh(Z), t(2, W, a, b, c, d, e, f)), Z == 2, W == 2, p(ho(k), U), U = t(V1, k, V2, _, _, _, _, _), V1 == V2, var(V1).",
        "set_prolog_flag(occurs_check, true), p(big, G), G == g(1, 2, 3, 4, 5, 6, 7, h(8, 9)), p(sh(1), T), T == t(1, 1, a, b, c, d, e, f), set_prolog_flag(occurs_check, false).",
        "p(h(1, 1), w).",
        "\\+ p(h(1, 2), w).",
        "p(h(V, 3), w), V == 3.",
        "forall(p(A, B), \\+ \\+ (numbervars(A-B, 0, _), write_canonical(A-B), nl)).",
        "forall(between(1, 60, _), (p(b, V), V == 2)), p(q, W), W == q, p(f(k), U), U == k.",
        "p(g(h(i)), j), \\+ p(g(h(x)), j), p(5000000000, N), N == [], p(S, 2.5), S == \"txt\".",
        @"forall(clause(p(A, B), Body), \+ \+ (numbervars(A-B-Body, 0, _), write_canonical(A-B-Body), nl)).",
        "clause(p(b, X), B), X == 2, B == true, clause(p(nope, Z), _), Z == nope, \\+ clause(p(nope, other), _).",
        "listing(p/2).",
        "retract(p(b, 2)), retract((p(X, X) :- B)), B == true, \\+ retract((p(a, 1) :- foo)).",
        @"retract(p(""txt"", F)), F =:= 2.5, retract(p([1, 2|T], T)), retract((p(f(Q), R) :- true)), Q == R.",
        "forall(p(A, B), \\+ \\+ (numbervars(A-B, 0, _), write_canonical(A-B), nl)).",
        "retract(p(_, _)), fail ; true.",
        "\\+ p(_, _).",
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FactsAsImagesAnswerAsFactsAsTermsDo(int threshold)
    {
        var images = Engine(threshold, images: true);
        var terms = Engine(threshold, images: false);
        foreach (string query in Script)
        {
            Assert.True(images.Query(query).Success, query);
            Assert.True(terms.Query(query).Success, query);
            string expected = Output(terms);
            Assert.Equal(expected, Output(images));
        }
        Assert.True(images.ImageFactsAsserted >= 9, "the facts were not asserted as images");
        Assert.Equal(0, terms.ImageFactsAsserted);
    }

    [Fact]
    public void AFactAssertedIsKeptAsItsImageAndItsTermIsBuiltOnDemand()
    {
        var e = Engine(0, images: true);
        Assert.True(e.Query("assertz(p(a, f(X, X))).").Success);
        var clause = Assert.Single(e.DynamicClausesFor(Fid("p", 2)));
        Assert.NotNull(clause.HeadImage);
        Assert.Equal(Shumway.Compiler.Ast.ClauseKind.Fact, clause.Kind);
        Assert.Equal("p(a, f(_G4, _G4))", clause.Term.ToString());
        Assert.Same(clause.Term, clause.Term);
    }

    [Fact]
    public void RetractKeyedOnTheFirstArgumentTriesTheCandidatesTheImagesKey()
    {
        var e = Engine(0, images: true);
        Assert.True(e.Query("between(1, 200, I), assertz(p(I, I)), fail ; true.").Success);
        Assert.True(e.Query(@"assertz(p(""key"", s)), assertz(p(k, s)), assertz(p([x], s)).").Success);
        long before = e.RetractCandidatesTried;
        Assert.True(e.Query("retract(p(150, V)), V == 150.").Success);
        Assert.Equal(1, e.RetractCandidatesTried - before);
        before = e.RetractCandidatesTried;
        Assert.True(e.Query("retract(p(k, s)).").Success);
        Assert.Equal(1, e.RetractCandidatesTried - before);
        // A packed string is a list: the key of a list call reaches it.
        before = e.RetractCandidatesTried;
        Assert.True(e.Query(@"retract(p([k, e, y], s)).").Success);
        Assert.True(e.RetractCandidatesTried - before <= 2);
        Assert.True(e.Query(@"\+ p(""key"", _), p([x], s).").Success);
    }

    [Fact]
    public void ClauseEnumerationTriesOnlyTheCandidatesTheImagesCannotRuleOut()
    {
        var e = Engine(0, images: true);
        Assert.True(e.Query(@"assertz(p(a, 1)), assertz(p(b, 2)), assertz(p(c, f(3))), assertz(p(d, ""txt"")).").Success);
        long before = e.ClauseCandidatesTried;
        Assert.True(e.Query("clause(p(b, X), B), X == 2, B == true.").Success);
        Assert.Equal(1, e.ClauseCandidatesTried - before);
        before = e.ClauseCandidatesTried;
        Assert.True(e.Query("clause(p(_, f(Y)), _), Y == 3.").Success);
        Assert.Equal(1, e.ClauseCandidatesTried - before);
        before = e.ClauseCandidatesTried;
        Assert.False(e.Query("clause(p(_, g(_)), _).").Success);
        // Packed text may be the list: it is tried, and nothing else is.
        Assert.Equal(1, e.ClauseCandidatesTried - before);
    }

    // A compound argument at the head threshold has an image of its own in
    // the pool, before its stream: the argument unbound, the copy builds it.
    [Fact]
    public void ALargeCompoundArgumentHasItsOwnImageBeforeItsStream()
    {
        // The query's own body images (its ground goal) are the same on both
        // engines: the fact's argument adds one on the images engine.
        var e = Engine(0, images: true);
        var terms = Engine(0, images: false);
        int images = e._literalPools.Images.Count, termImages = terms._literalPools.Images.Count;
        const string facts = "assertz(p(k, f(1, 2, 3, 4, 5, 6, 7, 8))), assertz(p(s, f(1, 2))).";
        Assert.True(e.Query(facts).Success);
        Assert.True(terms.Query(facts).Success);
        Assert.Equal(terms._literalPools.Images.Count - termImages + 1, e._literalPools.Images.Count - images);
        Assert.True(e.Query("p(k, X), X == f(1, 2, 3, 4, 5, 6, 7, 8), p(k, f(1, 2, 3, 4, 5, 6, 7, 8)), \\+ p(k, f(1, 2, 3, 4, 5, 6, 7, 9)).").Success);
        Assert.True(e.Query("p(k, f(A, 2, B, 4, 5, 6, 7, 8)), A == 1, B == 3, p(s, Y), Y == f(1, 2).").Success);
    }

    [Fact]
    public void WhatAnImageCannotCarryTakesTheTermPath()
    {
        var e = Engine(0, images: true);
        Assert.True(e.Query("assertz(p(123456789012345678901234567890, big)).").Success);
        Assert.True(e.Query("X is 1 rdiv 3, assertz(p(X, rat)).").Success);
        Assert.True(e.Query("assertz(m:p(mod, 1)), assertz((p(rule, Y) :- Y = r)).").Success);
        Assert.Equal(0, e.ImageFactsAsserted);
        foreach (var clause in e.DynamicClausesFor(Fid("p", 2))) Assert.Null(clause.HeadImage);
        Assert.True(e.Query("p(B, big), B =:= 123456789012345678901234567890, p(R, rat), R =:= 1 rdiv 3.").Success);
        Assert.True(e.Query("p(mod, 1), p(rule, Z), Z == r.").Success);
        Assert.True(e.Query("retract(p(123456789012345678901234567890, big)), \\+ p(_, big).").Success);
    }

    [Fact]
    public void AnAttributedVariableInAFactIsAPlainVariable()
    {
        var e = Engine(0, images: true);
        Assert.True(e.Query("put_attr(X, m, 1), assertz(p(X, X)).").Success);
        Assert.Equal(1, e.ImageFactsAsserted);
        Assert.True(e.Query("p(A, B), var(A), A == B, \\+ get_attr(A, m, _).").Success);
    }

    [Fact]
    public void TheLogicalUpdateViewHoldsOverImages()
    {
        var e = Engine(0, images: true);
        Assert.True(e.Query("assertz(p(1, n)), assertz(p(2, n)).").Success);
        Assert.True(e.Query("forall(p(X, n), (Y is X + 10, assertz(p(Y, n)))).").Success);
        Assert.True(e.Query("findall(X, p(X, n), L), L == [1, 2, 11, 12].").Success);
        Assert.True(e.Query("p(X, n), retract(p(X, n)), fail ; true.").Success);
        Assert.True(e.Query("\\+ p(_, n).").Success);
    }

    [Fact]
    public void TheImagePathIsOffForTheDebuggerAndForDeterministicModes()
    {
        var e = Engine(0, images: true);
        e.Flags.EmitDebugInfo = true;
        e.Flags.DebugCodegen = true;
        Assert.True(e.Query("assertz(p(dbg, 1)), p(dbg, X), X == 1.").Success);
        Assert.Equal(0, e.ImageFactsAsserted);

        var d = new PrologEngine { Out = new StringWriter() };
        d.ConsultString(":- dynamic(d/1).\n:- mode d(+) is det.");
        Assert.True(d.Query("assertz(d(1)), assertz(d(2)), d(2), \\+ d(3).").Success);
        Assert.Equal(0, d.ImageFactsAsserted);
    }

    // The image of a term whose arguments share a sub-term holds the block
    // once; its block scan visits each block once too, or the scan's stack
    // overflows on a term whose sharing multiplies the paths to a block.
    [Fact]
    public void AFactWhoseArgumentsShareSubTermsIsKeptAsAnImage()
    {
        var e = Engine(0, images: true);
        Assert.True(e.Query("""
            X1 = f(a, b, c), X2 = g(X1, X1, X1), X3 = g(X2, X2, X2), X4 = g(X3, X3, X3),
            assertz(p(X4, X4)),
            p(Z, W), Z == W, Z = g(A, B, C), A == B, B == C, A = g(D, E, F), D == E, E == F,
            D = g(G, H, I), G == H, H == I, G == f(a, b, c).
            """).Success);
        Assert.Equal(1, e.ImageFactsAsserted);
    }
}

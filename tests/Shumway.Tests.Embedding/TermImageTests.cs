using Shumway.Compiler.Wam;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-063: a ground term a body copies from an image is the term
/// the build gave, on both tiers, after a collection, after failing back
/// past it, from a bundle, and in a clause asserted at run time.</summary>
public sealed class TermImageTests
{
    private const string Program = """
        :- public lookup/2, mixed/1, text/1, loop/1, all/1.
        id(X, X).
        lookup(K, V) :- t([k(a, 1), k(b, 2), k(c, 3), k(d, 4), k(e, 5), k(f, 6)], K, V).
        t([k(K, V)|_], K, V) :- !.
        t([_|T], K, V) :- t(T, K, V).
        mixed(X) :- id(f([1, 2.5, "str", g(h(i)), [x, y|z]], 'A b', -3, 1000000000000), X).
        text(X) :- id(f("hello world", 'x', 1.5, [1, 2, 3]), X).
        loop(0) :- !.
        loop(N) :- \+ lookup(zz, _), N1 is N - 1, loop(N1).
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        all(Vs) :- findall(K-V, (mem(K, [a, b, c, d, e, f]), lookup(K, V)), Vs).
        """;

    private static readonly (string Goal, string Var)[] Goals =
    {
        ("lookup(c, V).", "V"),
        ("all(Vs).", "Vs"),
        ("mixed(X).", "X"),
        ("mixed(X), X = f(L, A, N, B), L = [_, F, S, G, T], F =:= 2.5, S == \"str\", G == g(h(i)), T == [x, y|z], A == 'A b', N =:= -3, B =:= 1000000000000, R = yes.", "R"),
        ("text(X), X = f(S, A, F, L), S == \"hello world\", A == x, F =:= 1.5, L == [1, 2, 3], R = yes.", "R"),
        ("mixed(X), garbage_collect, X = f(L, _, _, _), garbage_collect, L = [_, _, S|_], garbage_collect, S == \"str\", R = yes.", "R"),
        ("loop(2000), lookup(f, V).", "V"),
    };

    private static string Answer(PrologEngine e, string goal, string var)
    {
        var r = e.Query(goal);
        return r.Success ? $"{var} = {r.Bindings[var]}" : "false";
    }

    // The same program compiled with no image: every term built by
    // instructions. The threshold is the process's, so this holds it only
    // for the consult.
    private static PrologEngine Reference()
    {
        int threshold = ClauseCompiler.ImageThreshold;
        ClauseCompiler.ImageThreshold = 0;
        try
        {
            var e = new PrologEngine();
            e.IlPromotion.Threshold = 0;
            e.ConsultString(Program);
            return e;
        }
        finally { ClauseCompiler.ImageThreshold = threshold; }
    }

    private static bool Promoted(PrologEngine e, string indicator)
    {
        foreach (int fid in e.IlPromotion.PromotedFunctorIds())
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
            string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
            if ($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}" == indicator) return true;
        }
        return false;
    }

    [Fact]
    public void TheProgramCopiesItsGroundTermsFromImages()
    {
        // ANTI-VACUITY: the predicates under test do carry the instruction.
        foreach (string pred in new[] { "lookup", "mixed", "text" })
            Assert.Contains("put_blit",
                PredicateDisassembler.Disassemble(Program).First(e => e.Name == pred).Text);
    }

    [Fact]
    public void BothTiersGiveTheBuildsAnswers()
    {
        var reference = Reference();
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Program);
        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.IlPromotion.BackgroundCompilation = false;
        tiered.ConsultString(Program);
        foreach (var (goal, v) in Goals)
        {
            string expected = Answer(reference, goal, v);
            Assert.NotEqual("false", expected);
            Assert.Equal(expected, Answer(plain, goal, v));
            Assert.Equal(expected, Answer(tiered, goal, v));
        }
        foreach (string pi in new[] { "lookup/2", "mixed/1", "text/1" })
            Assert.True(Promoted(tiered, pi), $"{pi} did not promote\n{tiered.IlPromotion.DescribePromotionState()}");
        // Promoted, the answers are the compiled code's.
        foreach (var (goal, v) in Goals)
            Assert.Equal(Answer(reference, goal, v), Answer(tiered, goal, v));
    }

    [Fact]
    public void ABundleCarriesTheImages()
    {
        var reference = Reference();
        var bundle = new Bundle(new[] { new BundleEntry("images", Program) });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true, includeCompiledIl: true);
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(bytes));
        foreach (var (goal, v) in Goals)
            Assert.Equal(Answer(reference, goal, v), Answer(e, goal, v));
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(Promoted(e, "lookup/2"), e.IlPromotion.DescribePromotionState());
        foreach (var (goal, v) in Goals)
            Assert.Equal(Answer(reference, goal, v), Answer(e, goal, v));
    }

    [Fact]
    public void AClauseAssertedAtRunTimeCopiesItsImage()
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        // The image is interned by the assert and copied in the same query.
        Assert.True(e.Query("assertz((big(X) :- id([a, b, c, d, e, f, g, h, i, j], X))), big(L), L == [a, b, c, d, e, f, g, h, i, j].").Success);
        Assert.True(e.Query("big(L), L == [a, b, c, d, e, f, g, h, i, j].").Success);
        // The clause's source is what clause/2 shows.
        Assert.True(e.Query("clause(big(Y), id(L, Y)), L == [a, b, c, d, e, f, g, h, i, j].").Success);
    }
}

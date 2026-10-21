using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-063: a term a body copies from an image is the term the
/// build gave, its variables included (holes a register fills or receives,
/// a repeated one, an anonymous one, an attributed one), on both tiers,
/// after a collection, after failing back past it, from a bundle, and in a
/// clause asserted at run time.</summary>
public sealed class TermImageTests
{
    private const string Program = """
        :- public lookup/2, mixed/1, text/1, loop/1, all/1, pair/3, fresh/2, perm/2, attv/1, back/1.
        :- public tbl/2, tree/2, patt/3, hd/2, frz/2, ptbl/2, ptree/2, ppatt/3, phd/2.
        :- use_module(library(coroutining)).
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
        pair(X, Y, P) :- id(p(X, [a, b, c], Y, [d, e, f], X), P).
        fresh(P, Q) :- id(f(A, [1, 2, 3], B, g(A, B), _, _), P), id(h(A, B), Q).
        perm(P, Q) :- id(a, A), id(f(A, [x, y, z], B, B), P), id(B, Q).
        attv(P) :- put_attr(V, t, 1), id(f(V, [1, 2, 3], V), P).
        back(L) :- findall(P, (mem(X, [1, 2]), id(f(X, [a, b, c], X), P)), L).
        tbl(1, [a, b, c, d, e, f, g, h]).
        tbl(2, [i, j, k, l, m, n, o, p]).
        tree(t(l(1, 2), l(3, 4)), n(l(5, 6), l(7, 8))).
        patt(f(X, g(X, Y), [a, b, c]), Y, h(Y, X)).
        hd(V, f(V, [1, 2, 3], V)).
        frz(V, R) :- freeze(V, R = woke), tbl(2, V).
        ptbl(K, L) :- tbl(K, L).
        ptree(A, B) :- tree(A, B).
        ppatt(F, Y, H) :- patt(F, Y, H).
        phd(V, T) :- hd(V, T).
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
        ("pair(1, 2, P).", "P"),
        ("fresh(P, Q), P = f(A, L, B, G, _, _), L == [1, 2, 3], G == g(A, B), Q == h(A, B), var(A), var(B), R = yes.", "R"),
        ("perm(P, Q), P = f(a, [x, y, z], B1, B2), B1 == B2, B1 == Q, var(Q), R = yes.", "R"),
        ("attv(P), P = f(W, _, W2), W == W2, get_attr(W, t, A), R = A.", "R"),
        ("back(L).", "L"),
        ("fresh(P, _), garbage_collect, P = f(A, L, _, _, _, _), garbage_collect, L == [1, 2, 3], var(A), R = yes.", "R"),
        ("findall(P, (mem(X, [1, 2, 3]), pair(X, X, P)), Ps).", "Ps"),
        ("tbl(2, L).", "L"),
        ("ptbl(2, L).", "L"),
        ("ptbl(K, [i, j, k, l, m, n, o, p]).", "K"),
        ("ptbl(2, [i | T]).", "T"),
        ("findall(L, ptbl(_, L), Ls).", "Ls"),
        ("ptree(A, B), R = A-B.", "R"),
        ("ptree(t(l(1, 2), X), B), X = l(P, Q), R = P-Q-B.", "R"),
        ("ptree(A, n(l(5, 6), l(7, 8))), R = A.", "R"),
        ("\\+ ptree(t(l(1, 2), l(3, 5)), _), R = yes.", "R"),
        ("ppatt(F, y, H), R = F-H.", "R"),
        ("ppatt(f(1, g(1, 2), [a, b, c]), Y, H), R = Y-H.", "R"),
        ("phd(7, T).", "T"),
        ("tree(A, B), patt(F, y, H), hd(7, T), R = A-B-F-H-T.", "R"),
        ("frz(V, R).", "R"),
        ("tree(A, _), garbage_collect, A = t(l(P, _), _), R = P.", "R"),
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
        // back/1 builds its term inside the auxiliary predicate of its findall goal.
        var entries = PredicateDisassembler.Disassemble(Program);
        foreach (string pred in new[] { "lookup", "mixed", "text", "pair", "fresh", "perm", "attv" })
        {
            var own = entries.Where(e => e.Name.Contains(pred)).ToList();
            Assert.True(own.Any(e => e.Text.Contains("put_blit")),
                pred + " copies no image:\n" + string.Join("\n", own.Select(e => e.Name + "\n" + e.Text)));
        }
        // ...and the heads with large arguments carry the prefix.
        foreach (string pred in new[] { "tbl", "tree", "patt", "hd" })
            Assert.True(entries.Any(e => e.Name.Contains(pred) && e.Text.Contains("get_blit")), pred + " has no get_blit");
        // ...and the ones with variables carry images with holes of every kind.
        var pools = new LiteralPools();
        var compiler = new ClauseCompiler();
        foreach (var clause in new ClauseReader(Program).ReadAll())
            if (clause.Kind is ClauseKind.Rule or ClauseKind.Fact)
                compiler.Compile(clause, pools.Strings, pools.Floats, pools.BigInts, pools.Images);
        var holes = pools.Images.Items.SelectMany(i => i.Holes.ToArray()).ToList();
        Assert.Contains(holes, h => !h.IsOut && !h.IsY);
        Assert.Contains(holes, h => h.IsOut && !h.IsY);
        Assert.Contains(holes, h => !h.IsOut && h.IsY);
        Assert.Contains(holes, h => h.IsOut && h.IsY);
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
        // The head-image predicates themselves, not only their wrappers: a
        // get_blit the IL pre-scan does not know leaves them on Tier-0.
        foreach (string pi in new[] { "lookup/2", "mixed/1", "text/1", "pair/3", "fresh/2", "perm/2", "attv/1", "ptbl/2", "ptree/2", "ppatt/3", "phd/2", "tbl/2", "tree/2", "patt/3", "hd/2" })
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
        // With holes: the asserted clause's variables come and go through the copy.
        Assert.True(e.Query("assertz((wrap(X, Y, W) :- id(w(X, [a, b, c], Y, Y, _), W))), wrap(1, 2, W), W = w(1, L, 2, 2, V), L == [a, b, c], var(V).").Success);
    }
}

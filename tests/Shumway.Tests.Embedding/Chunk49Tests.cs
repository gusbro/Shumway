using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 49: Tier-1 IL gains <c>get_list</c> and <c>put_list</c> on top
/// of the chunk-48 compound support, so predicates that match or build
/// list heads (cons cells) now promote to IL.
///
/// <para>The remaining big-ticket IL features — non-tail <c>Call</c>,
/// PSTR-string opcodes (<c>get_pstr</c> needs the per-module literal
/// pool threaded into the compiler), and the tabling / true-AOT
/// scaffolds the chunk title gestures at — all need substantially more
/// engine plumbing and land in follow-up chunks. The IL list opcodes
/// here are the last natural extension before that bigger work.</para>
/// </summary>
public class Chunk49Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Cmp(string f, params Term[] a) => new CompoundTerm(f, a);

    private static Term List(params Term[] elems)
    {
        Term acc = Atom("[]");
        for (int i = elems.Length - 1; i >= 0; i--)
            acc = Cmp(".", elems[i], acc);
        return acc;
    }

    [Fact]
    public void Il_HeadMatchOnListPattern()
    {
        // head_first(X, [X|_]).
        // The head match decomposes [X|_] via get_list. IL must
        // dispatch read mode (since the caller supplies the list).
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public head_first/2.\nhead_first(X, [X|_]).");
        var sol = engine.Query("head_first(H, [a, b, c]).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("a"), sol["H"]);
    }

    [Fact]
    public void Il_HeadMatchOnLiteralList()
    {
        // Matching against an exact list literal.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public is_abc/1.\nis_abc([a, b, c]).");
        Assert.True(engine.Query("is_abc([a, b, c]).").Success);
        Assert.False(engine.Query("is_abc([a, b]).").Success);
        Assert.False(engine.Query("is_abc([x, y, z]).").Success);
    }

    [Fact]
    public void Il_HeadBuildsListWhenCallerSuppliesVar()
    {
        // Body-less fact with a list literal in head — when called with
        // an unbound arg, the head match runs in write mode and builds
        // the list on the heap.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public primes/1.\nprimes([2, 3, 5, 7]).");
        var sol = engine.Query("primes(P).");
        Assert.True(sol.Success);
        Assert.Equal(List(Int(2), Int(3), Int(5), Int(7)), sol["P"]);
    }

    [Fact]
    public void Il_NestedListPattern()
    {
        // Head with a deeper nested list.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public pair_list/1.\npair_list([[a, b], [c, d]]).");
        Assert.True(engine.Query("pair_list([[a, b], [c, d]]).").Success);
        Assert.False(engine.Query("pair_list([[a, b], [c, x]]).").Success);
    }

    [Fact]
    public void Il_DecomposesListHeadAndTail()
    {
        // first_rest(H, T, [H|T]) — both H and T bind to the caller's list.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(":- public first_rest/3.\nfirst_rest(H, T, [H|T]).");
        var sol = engine.Query("first_rest(H, T, [1, 2, 3]).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["H"]);
        Assert.Equal(List(Int(2), Int(3)), sol["T"]);
    }

    [Fact]
    public void Il_ListSameResultAsTier0()
    {
        var src = ":- public both/2.\nboth(X, [X, X, X]).";

        var tier0 = new PrologEngine();
        tier0.ConsultString(src);
        var sol0 = tier0.Query("both(hello, L).");

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(src);
        tier1.Query("both(_, _).");   // warm
        var sol1 = tier1.Query("both(hello, L).");

        Assert.Equal(sol0["L"], sol1["L"]);
    }
}

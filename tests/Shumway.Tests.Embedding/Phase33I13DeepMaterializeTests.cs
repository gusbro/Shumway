using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 I13 — materializing a deeply-nested <em>non-list</em> heap term into
/// a <see cref="Term"/> tree (<see cref="TermReader"/>) no longer overflows the
/// host. Chunk 111 made the list <em>spine</em> iterative, but general compound
/// nesting — <c>s(s(…z))</c>, a long left-associative <c>((…+2)+3)+…</c> — still
/// recursed one C# frame per level and stack-overflowed uncatchably at
/// materialise time (during <c>assertz</c>, or when a query's deep binding was
/// read back into a .NET <see cref="Term"/>). The whole walk is now an
/// explicit-stack post-order traversal.
///
/// <para>A crash here aborts the whole test host, so the real assertion is
/// "the process survives and the materialised term has the right shape". The
/// verification walks are themselves iterative — a naive recursive check would
/// re-introduce the very overflow under test.</para>
/// </summary>
public class Phase33I13DeepMaterializeTests
{
    // Well past the former ~2000-level overflow, small enough to stay fast.
    private const int Deep = 20000;

    private static PrologEngine NewEngine()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- dynamic pf/1.\n" +
            // s(s(...z)) — deep nesting through the single arg.
            "nest(0, z) :- !.\n" +
            "nest(N, s(T)) :- N > 0, N1 is N-1, nest(N1, T).\n" +
            // ((...(0+1)+2)...+N) — deep nesting through arg 0 (left-assoc).
            "lexp(0, 0) :- !.\n" +
            "lexp(N, P + N) :- N > 0, N1 is N-1, lexp(N1, P).\n" +
            ":- public store_nest/1.\n" +
            "store_nest(N) :- nest(N, T), assertz(pf(T)).\n");
        return e;
    }

    /// <summary>Depth of an <c>s(s(…z))</c> chain: count s/1 wrappers down to
    /// the <c>z</c> leaf, iteratively.</summary>
    private static int SNestDepth(Term? t)
    {
        int d = 0;
        while (t is CompoundTerm c && c.Functor == "s" && c.Args.Length == 1)
        {
            d++;
            t = c.Args[0];
        }
        Assert.True(t is AtomTerm { Name: "z" }, "s-nest did not bottom out at z");
        return d;
    }

    /// <summary>Depth of a left-associative <c>+</c> chain built by <c>lexp</c>:
    /// count '+'/2 nodes down arg 0 to the <c>0</c> leaf, iteratively.</summary>
    private static int LexpDepth(Term? t)
    {
        int d = 0;
        while (t is CompoundTerm c && c.Functor == "+" && c.Args.Length == 2)
        {
            d++;
            t = c.Args[0];
        }
        Assert.True(t is IntTerm { Value: 0 }, "lexp did not bottom out at 0");
        return d;
    }

    [Fact]
    public void DeepSNest_MaterializesAsQueryBinding()
    {
        // Reading the deep binding of T back into a .NET Term runs TermReader
        // during Query — the old recursive walk overflowed right here.
        var e = NewEngine();
        var sol = e.Query($"nest({Deep}, T).");
        Assert.True(sol.Success);
        Assert.Equal(Deep, SNestDepth(sol["T"]));
    }

    [Fact]
    public void DeepLeftAssocExpr_MaterializesAsQueryBinding()
    {
        var e = NewEngine();
        var sol = e.Query($"lexp({Deep}, T).");
        Assert.True(sol.Success);
        Assert.Equal(Deep, LexpDepth(sol["T"]));
    }

    [Fact]
    public void DeepSNest_AssertzThenReadBack()
    {
        // assertz materialises the clause term via TermReader too.
        var e = NewEngine();
        Assert.True(e.Query($"store_nest({Deep}).").Success);
        // The stored fact compiled and dispatches; read the deep arg back.
        var sol = e.Query("pf(T).");
        Assert.True(sol.Success);
        Assert.Equal(Deep, SNestDepth(sol["T"]));
    }

    [Fact]
    public void CyclicTerm_MaterializesWithMarkerNoOverflow()
    {
        // X = f(X) builds a cyclic structure (occurs-check-off =/2). The walk
        // must terminate with the synthetic _C cycle marker, not loop/overflow.
        var e = new PrologEngine();
        var sol = e.Query("X = f(X).");
        Assert.True(sol.Success);
        var t = sol["X"];
        Assert.True(t is CompoundTerm { Functor: "f", Args.Length: 1 });
        // The single arg is the cycle-back marker (a synthetic var), not
        // another f(...) that would imply we recursed into the cycle.
        var arg = ((CompoundTerm)t!).Args[0];
        Assert.True(arg is VarTerm, "cyclic self-reference should surface as a var marker");
    }

    [Fact]
    public void SharedAcyclicSubterm_MaterializesTwiceNotAsCycle()
    {
        // p(Y, Y) with Y = a(1): the two occurrences share a heap address but
        // are NOT a cycle. Path-scoped active-set removal must let both args
        // materialise as a(1) rather than one becoming a _C marker.
        var e = new PrologEngine();
        var sol = e.Query("Y = a(1), X = p(Y, Y).");
        Assert.True(sol.Success);
        var x = sol["X"] as CompoundTerm;
        Assert.NotNull(x);
        Assert.Equal("p", x!.Functor);
        Assert.Equal(2, x.Args.Length);
        foreach (var arg in x.Args)
        {
            var a = arg as CompoundTerm;
            Assert.NotNull(a);
            Assert.Equal("a", a!.Functor);
            Assert.True(a.Args[0] is IntTerm { Value: 1 });
        }
    }

    [Fact]
    public void ShallowTermsUnaffected()
    {
        // Ordinary mixed term: compound + list + scalars round-trips intact.
        var e = new PrologEngine();
        var sol = e.Query("T = foo(1, [a, b, c], bar(x, Y)).");
        Assert.True(sol.Success);
        var t = sol["T"] as CompoundTerm;
        Assert.NotNull(t);
        Assert.Equal("foo", t!.Functor);
        Assert.Equal(3, t.Args.Length);
        Assert.True(t.Args[0] is IntTerm { Value: 1 });
        // arg 1 is the list [a,b,c] as nested './2'.
        var list = t.Args[1] as CompoundTerm;
        Assert.NotNull(list);
        Assert.Equal(".", list!.Functor);
        // arg 2 is bar(x, Y).
        var bar = t.Args[2] as CompoundTerm;
        Assert.NotNull(bar);
        Assert.Equal("bar", bar!.Functor);
    }

    [Fact]
    public void DeepListStillWorks()
    {
        // Regression for the chunk-111 list-spine case, now served by the same
        // unified iterative walk.
        var e = new PrologEngine();
        var sol = e.Query($"numlist(1, {Deep}, L).");
        Assert.True(sol.Success);
        // Count the './2' spine iteratively.
        Term? t = sol["L"];
        int n = 0;
        while (t is CompoundTerm c && c.Functor == "." && c.Args.Length == 2)
        {
            n++;
            t = c.Args[1];
        }
        Assert.Equal(Deep, n);
        Assert.True(t is AtomTerm { Name: "[]" }, "list did not end in []");
    }
}

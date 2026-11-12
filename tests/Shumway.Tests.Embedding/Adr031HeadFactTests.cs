using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-031 head facts: a non-last clause that is its head alone runs that
/// head with no entry choice point. A head that fails goes on to the next
/// clause having pushed nothing; one that matches pushes the choice point
/// then, with the clause-entry marks, since the clauses after it are still
/// alternatives. member/2's helper is the case that matters: a choice point
/// per element tried, almost all of them failing. Tier-0 is the oracle; the
/// emission must be observationally identical, solutions and their order,
/// the bindings undone between them, wakeups and cuts.
/// </summary>
// Switches compiler statics for some of its tests: it runs alone, in the
// gate's exclusive pass.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public class Adr031HeadFactTests
{
    private const string Program = """
        :- use_module(library(coroutining)).
        :- public mem/2, lookup/3, alls/2, fz/2, once_mem/2, deep/1, twice/2.
        m3(_, X, X).
        m3([Y|T], X, _) :- m3(T, X, Y).
        mem(X, [Y|T]) :- m3(T, X, Y).
        % A head that takes the key-value pair apart into registers.
        lk(K, [K-V|_], V).
        lk(K, [_|T], V) :- lk(K, T, V).
        lookup(K, L, V) :- lk(K, L, V).
        alls(P, L) :- findall(P, mem(P, [p(1, a), q(2), p(3, b), p(1, c), p(4, a)]), L).
        % A wakeup that fails on some elements: the backtrack reaches the next one.
        fz(X, L) :- freeze(X, X \== b), mem(X, L).
        once_mem(X, L) :- mem(X, L), !.
        deep(N) :- numlist(1, N, L), mem(N, L).
        % A repeated variable: the first element binds X and then fails.
        twice(X, L) :- mem(g(X, X), L).
        % The bindings of V for each solution of G, by a failure loop.
        :- dynamic got/1, woke/1.
        :- public each/3, fzp/2, fze/2, dfm/2.
        each(V, G, Vs) :- retractall(got(_)), ( call(G), assertz(got(V)), fail ; true ), findall(X, got(X), Vs).
        % freeze on the pattern: only the elements that match wake it.
        fzp(L, Ws) :- retractall(woke(_)), freeze(Y, assertz(woke(Y))),
            ( mem(p(Y, z), L), fail ; true ), findall(W, woke(W), Ws).
        % freeze on an element: a constant pattern binds it, and wakes it.
        fze(Hs, Ws) :- retractall(woke(_)), freeze(E, assertz(woke(E))),
            each(h, mem(k, [E, k]), Hs), findall(W, woke(W), Ws).
        dfm(X, L) :- dif(X, a), mem(X, L).
        """;

    // Tier1Region: runtime promotion, where a multi-clause root is a region
    // of one. Tier1Method: the same with regions off, a method per predicate
    // as a baked library has. Tier1Bundle: the persisted IL.
    public enum Mode { Tier0, Tier1Region, Tier1Method, Tier1Bundle }
    public static TheoryData<Mode> Modes => new()
    { Mode.Tier0, Mode.Tier1Region, Mode.Tier1Method, Mode.Tier1Bundle };

    private static readonly string[] Warm =
    {
        "alls(_, _).", "lookup(k, [k-1], _).", "fz(_, [a]).",
        "once_mem(_, [a]).", "deep(3).", "twice(_, [g(1, 1)]).",
    };

    private static PrologEngine Engine(Mode m)
    {
        switch (m)
        {
            case Mode.Tier0:
            {
                var e = new PrologEngine();
                e.IlPromotion.Threshold = 0;
                e.ConsultString(Program);
                return e;
            }
            case Mode.Tier1Region:
            case Mode.Tier1Method:
            {
                bool regions = IlPredicateCompiler.RegionCompile;
                bool single = IlPredicateCompiler.OneMemberRegions;
                long heads = IlPredicateCompiler.CpFreeGuardStats.HeadFact;
                int quick = IlPredicateCompiler.GuardQuickRejectSites;
                if (m == Mode.Tier1Method)
                {
                    IlPredicateCompiler.RegionCompile = false;
                    IlPredicateCompiler.OneMemberRegions = false;
                }
                try
                {
                    var e = new PrologEngine();
                    e.IlPromotion.Threshold = 1;
                    e.ConsultString(Program);
                    // Every predicate called once, then promoted, so the asserts run IL.
                    foreach (string g in Warm) e.Query(g);
                    Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
                    // ANTI-VACUITY: the code took the head-fact path.
                    Assert.True(IlPredicateCompiler.CpFreeGuardStats.HeadFact > heads,
                        "no head fact compiled without a choice point");
                    Assert.True(IlPredicateCompiler.GuardQuickRejectSites > quick,
                        "no head fact compiled with the quick reject");
                    return e;
                }
                finally
                {
                    IlPredicateCompiler.RegionCompile = regions;
                    IlPredicateCompiler.OneMemberRegions = single;
                }
            }
            default:
            {
                var bundle = new Bundle(new[] { new BundleEntry("headfact", Program) });
                byte[] bytes = BundleWriter.ToBytes(bundle,
                    includeCompiledBytecode: true, includeCompiledIl: true);
                var e = new PrologEngine();
                e.LoadBundle(BundleReader.FromBytes(bytes));
                return e;
            }
        }
    }

    private static string[] All(PrologEngine e, string goal, string var)
        => e.QueryAll(goal).Select(s => s[var]!.ToString()!).ToArray();

    [Theory]
    [MemberData(nameof(Modes))]
    public void EverySolutionInOrder(Mode m)
    {
        var e = Engine(m);
        Assert.True(e.Query("alls(p(X, Y), L), L == [p(1, a), p(3, b), p(1, c), p(4, a)].").Success);
        Assert.Equal(new[] { "1", "3", "1", "4" }, All(e, "mem(p(X, _), [p(1, a), q(2), p(3, b), p(1, c), p(4, a)]).", "X"));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AHeadThatTakesTermsApartMatchesEachKey(Mode m)
    {
        var e = Engine(m);
        Assert.Equal(new[] { "2", "4" }, All(e, "lookup(k, [j-1, k-2, l-3, k-4], V).", "V"));
        Assert.False(e.Query("lookup(z, [j-1, k-2], _).").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TheBindingsOfAFailedHeadAreUndone(Mode m)
    {
        var e = Engine(m);
        Assert.Equal(new[] { "3" }, All(e, "twice(X, [g(1, 2), g(3, 3), g(4, 5)]).", "X"));
        Assert.True(e.Query("mem(f(X, Y), [f(1, a), f(2, b)]), X == 2, Y == b.").Success);
    }

    /// <summary>The quick reject turns away a head whose cells already
    /// differ, before the guard opens. It must turn away only what the
    /// unifier fails: each case here has elements it decides (atoms,
    /// functors, a first argument a level or two down, cells of different
    /// tags) beside elements it leaves to the unifier (a clash further in,
    /// numbers kept off-cell, packed strings, a cyclic term).</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void TheQuickRejectFailsOnlyWhatTheUnifierFails(Mode m)
    {
        var e = Engine(m);
        Assert.False(e.Query(@"mem(zz, [a, b, c]).").Success);
        Assert.Single(e.QueryAll(@"mem(b, [a, b, c])."));
        Assert.Equal(new[] { "2", "4" },
            All(e, @"mem(state(X), [piece(1), state(2), piece(3), state(4)]).", "X"));
        Assert.Equal(new[] { "b" },
            All(e, @"mem(piece(z-9, C), [piece(a-1, w), piece(z-9, b), piece(z-8, w), piece(y-9, w)]).", "C"));
        Assert.Equal(new[] { "1", "3" },
            All(e, @"mem(f(g(h(k(X)))), [f(g(h(k(1)))), f(g(h(j(2)))), f(g(h(k(3))))]).", "X"));
        Assert.Equal(new[] { "2" }, All(e, @"mem(p(X, a), [p(1, b), p(2, a)]).", "X"));
        Assert.Single(e.QueryAll(@"mem(1, [a, 1.0, [0'1], f(1), 1])."));
        Assert.Single(e.QueryAll(@"mem(100000000000000000000, [1, 100000000000000000001, 100000000000000000000])."));
        Assert.Equal(2, e.QueryAll(@"mem(2.5, [1.5, 2.5, 2.50]).").Count());
        Assert.Equal(2, e.QueryAll(@"mem(""ab"", [""ab"", ""cd"", [a, b]]).").Count());
        Assert.True(e.Query(@"X = f(X), each(s, mem(X, [f(f(a)), X, f(X)]), Ss), Ss == [s, s].").Success);
    }

    /// <summary>An attributed variable is left to the unifier on either
    /// side, so its wakeups run as they did: once per element it is bound to
    /// in a head that matches, and never for a head that fails.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void TheQuickRejectLeavesAttributedVariablesToTheUnifier(Mode m)
    {
        var e = Engine(m);
        Assert.True(e.Query(@"fzp([p(1, a), p(2, z), p(3, a), p(4, z)], Ws), Ws == [2, 4].").Success);
        Assert.True(e.Query(@"fze(Hs, Ws), Hs == [h, h], Ws == [k].").Success);
        Assert.True(e.Query(@"each(X, dfm(X, [a, b, a, c]), Xs), Xs == [b, c].").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AFailingWakeupBacktracksIntoTheNextElement(Mode m)
    {
        var e = Engine(m);
        Assert.Equal(new[] { "a", "c", "d" }, All(e, "fz(X, [a, b, c, b, d]).", "X"));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ACutAfterTheMatchCommitsToIt(Mode m)
    {
        var e = Engine(m);
        Assert.Equal(new[] { "1" }, All(e, "once_mem(p(X), [q, p(1), p(2)]).", "X"));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TheLastElementLeavesNoChoicePoint(Mode m)
    {
        var e = Engine(m);
        Assert.Single(e.QueryAll("deep(20000)."));
    }

    [Fact]
    public void TheRecognizerTakesAHeadFactOnlyWhenAskedTo()
    {
        // Clause 0 found by its first instruction: the dispatch ahead of it
        // depends on how the clauses index.
        static int First(byte[] code, Opcode op)
        {
            for (int at = 0; at < code.Length;
                 at += code[at] == (byte)Opcode.Meta ? 6 : OpcodeTable.Get(code[at]).Size)
                if ((Opcode)code[at] == op) return at;
            return -1;
        }
        var pc = new Shumway.Compiler.Wam.PredicateCompiler();
        var cp = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "k(X, X). k(X, f(X)).").ReadAll().ToList());
        int k0 = First(cp.BytecodeUnfused, Opcode.GetValueX);
        // A head fact is no cut-shaped guard: the default stays as it was.
        Assert.False(IlPredicateCompiler.TryGetCpFreeGuard(
            cp.BytecodeUnfused, k0, cp.BytecodeUnfused.Length, 2,
            calleeMap: null, cp.CallSites, out _, suppressStats: true));
        Assert.True(IlPredicateCompiler.TryGetCpFreeGuard(
            cp.BytecodeUnfused, k0, cp.BytecodeUnfused.Length, 2,
            calleeMap: null, cp.CallSites, out var g, suppressStats: true,
            allowKeepAlternative: true));
        Assert.True(g.KeepsAlternative);
        Assert.Equal((byte)Opcode.Proceed, cp.BytecodeUnfused[g.CutPc]);
        Assert.True(g.NeedsSnapshot);

        // A clause whose head can never fail gains nothing and stays out.
        var any = pc.Compile(new Shumway.Compiler.Parsing.ClauseReader(
            "b(_, _). b(_, x).").ReadAll().ToList());
        Assert.False(IlPredicateCompiler.TryGetCpFreeGuard(
            any.BytecodeUnfused, First(any.BytecodeUnfused, Opcode.Proceed), any.BytecodeUnfused.Length, 2,
            calleeMap: null, any.CallSites, out _, suppressStats: true,
            allowKeepAlternative: true));
    }
}

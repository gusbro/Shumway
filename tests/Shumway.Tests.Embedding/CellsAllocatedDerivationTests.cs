using System.IO;
using System.Linq;
using Shumway.Builtins;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><see cref="Activation.CellsAllocated"/>, the deterministic metric
/// behind the harness's <c>--alloc</c> mode, is derived: the heap top plus the
/// cells every lowering of the heap top gave back to the count (a choice
/// point's restore on either tier, a guard's failure, a catch frame's unwind,
/// a builtin's trial rollback, the collector's compaction, the wasm tier's
/// sync). The numbers here are what the per-allocation counter produced on the
/// same programs before the derivation, pinned on both tiers: a lowering site
/// that forgets to account shows as a lower count. A change that means to add
/// or remove heap allocations updates them, as it would a <c>--alloc</c>
/// baseline (the wakeups row moved by eight with the module-owned dynamics,
/// PR #215, which the per-allocation counter on that main confirmed).</summary>
public sealed class CellsAllocatedDerivationTests
{
    static CellsAllocatedDerivationTests()
    {
        BuiltinsRegistry.Register("$cad_cells", 1,
            engine => engine.UnifyRegisterWithCell(0, Cell.Int(engine.CellsAllocated)));
        BuiltinsRegistry.Register("$cad_gc_runs", 1,
            engine => engine.UnifyRegisterWithCell(0, Cell.Int(engine.HeapGcRuns)));
        BuiltinsRegistry.Register("$cad_gc_reclaimed", 1,
            engine => engine.UnifyRegisterWithCell(0, Cell.Int(engine.HeapGcReclaimedCells)));
    }

    private const string Corpus = """
        :- use_module(library(coroutining)).
        :- dynamic(fact/2).
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).
        range(N, N, [N]) :- !.
        range(I, N, [I|T]) :- I < N, I1 is I + 1, range(I1, N, T).
        build(0, []) :- !.
        build(N, [N|T]) :- N1 is N - 1, build(N1, T).
        sel(X, [X|T], T).
        sel(X, [H|T], [H|R]) :- sel(X, T, R).
        perm([], []).
        perm(L, [X|P]) :- sel(X, L, R), perm(R, P).
        kind(X, K) :- X = f(A), A > 2, !, K = big.
        kind(X, K) :- X = f(A), A > 0, !, K = small.
        kind(_, none).
        wide(X, K) :- X = [A|_], L = [A, A, A], length(L, N), N > 5, !, K = N.
        wide(X, K) :- X = [A|_], L = [A, A], length(L, K).
        ite(X, Y) :- ( build(X, L), length(L, N), N > 100 -> Y = long ; Y = short ).
        first(X, L) :- member(X, L), !.
        deep(X) :- ( X = 1 ; X = 2 ; X = 3 ), X > 1, !.
        thrower(N) :- build(N, L), length(L, _), throw(oops(N)).
        catcher(N) :- catch(thrower(N), oops(_), true).
        colls(L1, L2, L3) :-
            findall(X-Y, (member(X, [c, a, b]), member(Y, [2, 1])), L1),
            bagof(X, member(X, [c, a, b, a]), L2),
            setof(X, member(X, [c, a, b, a]), L3).
        gcprog(N) :- build(N, _), build(N, L), garbage_collect, length(L, N).
        churn(0) :- !.
        churn(N) :- build(20000, _), N1 is N - 1, churn(N1).
        asserts(N) :-
            forall(between(1, N, I), assertz(fact(I, [I, I]))),
            findall(V, fact(_, V), Vs), length(Vs, N), retractall(fact(_, _)).
        frz(L) :- freeze(X, (X > 2 -> L = big ; L = small)), ( X = 1 ; X = 5 ), L == big.
        difs(L) :- dif(X, Y), findall(X-Y, (member(X, [1, 2]), member(Y, [1, 2])), L).
        trials(L) :- f(_, b) \= f(a, c), \+ f(a, _) \= f(_, b), sort([c, a, b, a], L),
            recorda(k, f(1, _)), recorda(k, g(2)), findall(T, recorded(k, T), Ts), length(Ts, 2),
            ( recorded(k, R, Ref), R = f(_, _), erase(Ref), fail ; true ),
            findall(T2, recorded(k, T2), [g(2)]), ( recorded(k, _, Ref2), erase(Ref2), fail ; true ).
        bigs :- B is 2^61, S is B * 1, S =:= 2^61, T is B * 3, U is T * 0, U =:= 0,
            V is B * B, V > T.
        """;

    // Every path that lowers the heap top, and the reads that must agree.
    private static readonly (string Label, string Goal)[] Goals =
    {
        ("nrev", "range(1, 300, L), nrev(L, R), R = [300|_]."),
        ("perm", "findall(P, perm([1, 2, 3, 4, 5, 6], P), Ps), length(Ps, 720)."),
        ("faildrive", "( between(1, 2000, I), range(1, 5, L), nrev(L, _), I > 3000 ; true )."),
        ("guards", "findall(K, (member(X, [f(1), f(5), g, f(3)]), kind(X, K)), Ks), Ks == [small, big, none, big],"
                   + " findall(W, (member(Y, [[1], [2, 3]]), wide(Y, W)), Ws), Ws == [2, 2]."),
        ("ite", "findall(Y, (member(N, [5, 150, 7, 200]), ite(N, Y)), Ys), Ys == [short, long, short, long]."),
        ("cut", "findall(X, (member(L, [[a, b], [c, d]]), first(X, L)), Xs), Xs == [a, c], deep(D), D == 2."),
        ("catch", "forall(between(1, 50, I), catcher(I)), catcher(5000)."),
        ("colls", "colls(L1, L2, L3), length(L1, 6), L2 == [c, a, b, a], L3 == [a, b, c],"
                  + " findall(K-Vs, bagof(V, member(K-V, [1-a, 2-b, 1-c]), Vs), Gs), Gs == [1-[a, c], 2-[b]]."),
        ("gc", "gcprog(30000), '$cad_gc_runs'(R), R >= 1, '$cad_gc_reclaimed'(C), C >= 60000."),
        ("autogc", "churn(60), '$cad_gc_runs'(R), R >= 1."),
        ("asserts", "asserts(500), ( between(1, 300, I), assertz(fact(I, x)), fail ; true ),"
                    + " ( retract(fact(_, _)), fail ; true ), \\+ fact(_, _)."),
        ("wakeups", "frz(L), L == big, difs(D), length(D, 2)."),
        ("trials", "trials(L), L == [a, b, c]."),
        ("bigs", "bigs."),
        ("midquery", "'$cad_cells'(C0), gcprog(2000), catcher(300), '$cad_cells'(C1), D is C1 - C0, D > 0."),
    };

    private const string Expected = """
        nrev 91202 91202
        perm 19878 19878
        faildrive 92002 92002
        guards 80 80
        ite 763 763
        cut 24 24
        catch 13265 13265
        colls 326 326
        gc 120005 120005
        autogc 2400061 2400061
        asserts 20619 20619
        wakeups 573 579
        trials 104 104
        bigs 13 13
        midquery 8620 8620

        """;

    private static PrologEngine Engine(int ilThreshold)
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = ilThreshold;
        e.ConsultString(Corpus);
        return e;
    }

    private static long[] Measure(PrologEngine e)
    {
        var cells = new long[Goals.Length];
        for (int i = 0; i < Goals.Length; i++)
        {
            Assert.True(e.Query(Goals[i].Goal).Success, Goals[i].Label);
            cells[i] = e.LastQueryCellsAllocated;
        }
        return cells;
    }

    private static string Name(int fid)
    {
        var (atomId, arity) = FunctorTable.Lookup(fid);
        string name = AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    [Fact]
    public void TheCountIsTheOneThePerAllocationCounterGave()
    {
        var plain = Engine(0);
        long[] tier0 = Measure(plain);
        // The same engine again: a count is a property of the query, not of
        // what ran on the engine before it.
        Assert.Equal(tier0, Measure(plain));

        var tiered = Engine(1);
        string[] promoted = { "app/3", "nrev/2", "range/3", "build/2", "sel/3", "perm/2",
            "kind/2", "wide/2", "ite/2", "first/2", "deep/1", "thrower/1", "catcher/1", "churn/1" };
        var seen = new HashSet<string>();
        for (int round = 0; round < 5 && !promoted.All(seen.Contains); round++)
        {
            Measure(tiered);
            Assert.True(tiered.IlPromotion.WaitForPendingPromotions(120_000), "promotion did not settle");
            seen = tiered.IlPromotion.PromotedFunctorIds().Select(Name).ToHashSet();
        }
        foreach (string pi in promoted)
            Assert.True(seen.Contains(pi), $"{pi} did not promote");
        long[] tier1 = Measure(tiered);

        var table = new System.Text.StringBuilder();
        for (int i = 0; i < Goals.Length; i++)
            table.Append(Goals[i].Label).Append(' ').Append(tier0[i]).Append(' ').Append(tier1[i]).Append('\n');
        string actual = table.ToString();
        // The literal takes the checkout's line endings; the table is built with '\n'.
        Assert.True(Expected.Replace("\r\n", "\n") == actual, "\n" + actual);
    }

    /// <summary>A solution enumeration's activation lives across its answers:
    /// the count at the end is one query's, however many times the machine
    /// backtracked into it.</summary>
    [Fact]
    public void AnEnumerationCountsAsOneQuery()
    {
        var e = Engine(0);
        Assert.Equal(24, e.QueryAll("perm([1, 2, 3, 4], _).").Count());
        long enumerated = e.LastQueryCellsAllocated;
        Assert.True(e.Query("findall(P, perm([1, 2, 3, 4], P), Ps), length(Ps, 24).").Success);
        long collected = e.LastQueryCellsAllocated;
        Assert.True(enumerated > 0 && collected > 0, $"{enumerated} / {collected}");
        Assert.Equal(enumerated, e.QueryAll("perm([1, 2, 3, 4], _).").Count() == 24 ? e.LastQueryCellsAllocated : -1);
    }
}

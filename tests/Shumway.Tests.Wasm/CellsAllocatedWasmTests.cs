using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The wasm tier claims cells on its own and hands the tally back at
/// every crossing, where the engine folds it into <see cref="Shumway.Core.Activation.CellsAllocated"/>,
/// now derived from the heap top. Pinned to what the per-allocation counter
/// gave on the same programs, as the managed tiers' numbers are in the
/// Embedding suite.</summary>
public sealed class CellsAllocatedWasmTests
{
    private const string Corpus = """
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
        ite(X, Y) :- ( build(X, L), length(L, N), N > 100 -> Y = long ; Y = short ).
        thrower(N) :- build(N, L), length(L, _), throw(oops(N)).
        catcher(N) :- catch(thrower(N), oops(_), true).
        gcprog(N) :- build(N, _), build(N, L), garbage_collect, length(L, N).
        """;

    private static readonly (string Label, string Goal)[] Goals =
    {
        ("nrev", "range(1, 300, L), nrev(L, R), R = [300|_]."),
        ("perm", "findall(P, perm([1, 2, 3, 4, 5, 6], P), Ps), length(Ps, 720)."),
        ("faildrive", "( between(1, 2000, I), range(1, 5, L), nrev(L, _), I > 3000 ; true )."),
        ("guards", "findall(K, (member(X, [f(1), f(5), g, f(3)]), kind(X, K)), Ks), Ks == [small, big, none, big]."),
        ("ite", "findall(Y, (member(N, [5, 150, 7, 200]), ite(N, Y)), Ys), Ys == [short, long, short, long]."),
        ("catch", "forall(between(1, 50, I), catcher(I)), catcher(5000)."),
        ("gc", "gcprog(30000)."),
    };

    private const string Expected = """
        nrev 91202 91202
        perm 19878 19878
        faildrive 92002 92002
        guards 33 33
        ite 763 763
        catch 13265 13265
        gc 120003 120003

        """;

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

    [Fact]
    public void TheCountIsTheOneThePerAllocationCounterGave()
    {
        var plain = new PrologEngine { Out = new StringWriter() };
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        long[] tier0 = Measure(plain);

        var (tiered, members) = TieredEngine.Build(Corpus);
        tiered.Out = new StringWriter();
        Measure(tiered);   // every predicate promotes on its first call
        Assert.True(members.Count >= 10, $"{members.Count} members promoted");
        long[] wasm = Measure(tiered);

        var table = new System.Text.StringBuilder();
        for (int i = 0; i < Goals.Length; i++)
            table.Append(Goals[i].Label).Append(' ').Append(tier0[i]).Append(' ').Append(wasm[i]).Append('\n');
        string actual = table.ToString();
        // The literal takes the checkout's line endings; the table is built with '\n'.
        Assert.True(Expected.Replace("\r\n", "\n") == actual, "\n" + actual);
    }
}

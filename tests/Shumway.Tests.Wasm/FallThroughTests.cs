using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A case that ends jumping to the next case of its partition falls
/// into it rather than going round the dispatch loop: a try's first clause, an
/// alternative's next one. Counted with the module's own dispatch counter,
/// so the comparison is the code's and not a clock's.</summary>
public sealed class FallThroughTests(ITestOutputHelper o)
{
    private const string Corpus = """
        range(N, N, [N]) :- !.
        range(I, N, [I|T]) :- I < N, I1 is I + 1, range(I1, N, T).
        queens(N, Qs) :- range(1, N, Ns), perm(Ns, Qs), safe(Qs).
        perm([], []).
        perm(L, [H|T]) :- sel(H, L, R), perm(R, T).
        sel(X, [X|T], T).
        sel(X, [H|T], [H|R]) :- sel(X, T, R).
        safe([]).
        safe([Q|Qs]) :- noattack(Q, Qs, 1), safe(Qs).
        noattack(_, [], _).
        noattack(Q, [Q1|Qs], D) :- Q =\= Q1 + D, Q =\= Q1 - D, D1 is D + 1, noattack(Q, Qs, D1).
        """;

    private const string Goal = "findall(Qs, queens(6, Qs), L), length(L, N).";

    private static (long Dispatches, string Answer) Run(bool fallThrough)
    {
        bool saved = WasmPredicateCompiler.FallThrough;
        WasmPredicateCompiler.FallThrough = fallThrough;
        WasmPredicateCompiler.CountTransfers = true;
        try
        {
            var (engine, members) = TieredEngine.Build(Corpus);
            Assert.True(engine.Query(Goal).Success);
            Assert.NotEmpty(members);
            WasmTierDelegate.ResetDiag();
            var r = engine.Query(Goal);
            Assert.True(r.Success);
            return (WasmTierDelegate.DiagDispatches, r.Bindings["N"].ToString()!);
        }
        finally
        {
            WasmPredicateCompiler.FallThrough = saved;
            WasmPredicateCompiler.CountTransfers = false;
        }
    }

    [DiagFact]
    public void ACaseFallsIntoTheNextOneWithoutADispatch()
    {
        var round = Run(fallThrough: false);
        var fall = Run(fallThrough: true);
        o.WriteLine($"dispatches: {round.Dispatches} round the loop, {fall.Dispatches} falling through");
        Assert.Equal("4", round.Answer);
        Assert.Equal(round.Answer, fall.Answer);
        Assert.True(fall.Dispatches < round.Dispatches,
            $"falling through dispatched as often ({fall.Dispatches} against {round.Dispatches})");
    }
}

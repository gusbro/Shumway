using Shumway.Compiler.Wasm;
using Shumway.Embedding;

namespace Shumway.Tests.Wasm;

/// <summary>The wasm tier against the LIVE engine: promotion through the
/// ordinary dispatch path, execution through <see cref="DesktopWasmRunner"/>
/// (the copy-image runner), verdicts through <see cref="WasmTierDelegate"/>.
/// Everything the interpreter does around the delegate -- marker resumes,
/// backtracking into wasm choice points, builtins, deopt -- is the REAL
/// machinery; only the wasm execution engine differs from the browser.</summary>
public class EngineWasmTierTests
{
    private const string Corpus = """
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).
        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        max(X, Y, X) :- X >= Y, !.
        max(_, Y, Y).
        classify(X, neg) :- X < 0, !.
        classify(0, zero) :- !.
        classify(_, pos).
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).
        pi(3.14159).
        mkp(T, T).
        wrap(X, R) :- mkp(f(g(X), h(X)), R).
        unwrap(f(g(A), h(B)), A, B).
        rt(X, A, B) :- wrap(X, T), unwrap(T, A, B).
        same(X, X).
        poly(X, R) :- R is X*X + 3*X.
        """;

    private static PrologEngine WasmEngine()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        var store = engine.IlPromotion;
        store.Threshold = 0;                     // the IL tier stands aside
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,                       // promote on first dispatch
            Promoter = (pred, linkedBase) =>
            {
                try
                {
                    var env = new EngineWasmCompileEnv(pred.FunctorId, linkedBase);
                    var entry = WasmPredicateCompiler.Compile(pred, env,
                        floatLiterals: store.FloatPoolProvider?.Invoke(pred.FunctorId));
                    int maxCursor = 0;
                    foreach (var (_, c) in entry.CursorByAddress)
                        if (c > maxCursor) maxCursor = c;
                    var addrByCursor = new int[maxCursor + 1];
                    foreach (var (addr, c) in entry.CursorByAddress)
                        addrByCursor[c] = linkedBase + addr;
                    var runner = new DesktopWasmRunner(entry.Module);
                    return new WasmTierDelegate(pred.FunctorId, runner, addrByCursor).Invoke;
                }
                catch (WasmCompileException)
                {
                    return null;
                }
            },
        };
        return engine;
    }

    [Fact]
    public void RecursionCallsAndArithmeticThroughTheTier()
    {
        var e = WasmEngine();
        Assert.True(e.Query("nrev([1,2,3,4,5], R), R == [5,4,3,2,1].").Success);
        Assert.True(e.Query("len([a,b,c,d], 4).").Success);
        Assert.True(e.Query("poly(5, 40).").Success);
        Assert.False(e.Query("poly(5, 41).").Success);
        // The tier actually ran (anti-vacuity): nrev, app, len and poly all
        // crossed the threshold. Promoted ids are the consulted predicates'
        // module-scoped functors, so count them rather than re-intern names.
        Assert.True(e.IlPromotion.PromotedFunctorIds().Count() >= 4,
            $"promoted: [{string.Join(",", e.IlPromotion.PromotedFunctorIds())}]");
    }

    [Fact]
    public void CutCommitsAndBacktrackingEnumerates()
    {
        var e = WasmEngine();
        Assert.True(e.Query("max(3, 5, 5), max(5, 3, 5), classify(-2, neg), classify(0, zero).").Success);
        // findall drives backtracking through wasm choice points from the
        // interpreter side (Fail -> marker BP -> retry cursor).
        Assert.True(e.Query("findall(X, mem(X, [1,2,3]), [1,2,3]).").Success);
        Assert.True(e.Query("findall(M, max(2, 9, M), [9]).").Success);
    }

    [Fact]
    public void FloatsReservedBuildsAndTheGeneralUnifier()
    {
        var e = WasmEngine();
        Assert.True(e.Query("pi(X), X > 3.14, X < 3.15.").Success);
        Assert.True(e.Query("rt(7, 7, 7).").Success);
        Assert.True(e.Query("wrap(1, T), same(T, f(g(1), h(1))).").Success);
        Assert.False(e.Query("wrap(1, T), same(T, f(g(1), h(2))).").Success);
    }

    [Fact]
    public void AControlEngineAgreesOnEverything()
    {
        var control = new PrologEngine();
        control.ConsultString(Corpus);
        foreach (var goal in new[]
        {
            "nrev([1,2,3,4,5], [5,4,3,2,1])",
            "poly(5, 40)",
            "findall(X, mem(X, [1,2,3]), [1,2,3])",
            "rt(7, 7, 7)",
        })
            Assert.True(control.Query(goal + ".").Success);
    }

}

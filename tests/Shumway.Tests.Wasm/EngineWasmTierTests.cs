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
        var world = new DesktopWasmWorld();
        // The GROUP promoter: every promotion recompiles the whole set into
        // one module (cross-member calls become internal jumps) and installs
        // the fresh build; delegates resolve against the world per entry.
        var members = new List<WasmGroupMember>();
        var env = new EngineWasmCompileEnv();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,                       // promote on first dispatch
            Promoter = (pred, linkedBase) =>
            {
                var candidate = new WasmGroupMember(pred, linkedBase,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId));
                members.Add(candidate);
                try
                {
                    var entry = WasmPredicateCompiler.CompileGroup(members, env);
                    var entryAddr = new Dictionary<int, int>(members.Count);
                    foreach (var m in members)
                        entryAddr[m.Predicate.FunctorId] = m.Bias;
                    world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                        entry.CursorByAddress, entryAddr, entry.RegisterDemand);
                    return new WasmTierDelegate(pred.FunctorId, world).Invoke;
                }
                catch (WasmCompileException)
                {
                    // The candidate poisoned the group: recompile without it.
                    members.Remove(candidate);
                    if (members.Count > 0)
                    {
                        var entry = WasmPredicateCompiler.CompileGroup(members, env);
                        var entryAddr = new Dictionary<int, int>(members.Count);
                        foreach (var m in members)
                            entryAddr[m.Predicate.FunctorId] = m.Bias;
                        world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                            entry.CursorByAddress, entryAddr, entry.RegisterDemand);
                    }
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
    public void InGroupCallsNeverLeaveTheModule()
    {
        // The group design's contract: once the predicates share a module,
        // a cross-member call (tail or NON-tail) is an internal dispatch
        // jump -- no chain switch, no host boundary. Warm first so every
        // predicate is promoted into the group, then measure one run each.
        var e = WasmEngine();
        Assert.True(e.Query("nrev([1,2,3], R), R == [3,2,1].").Success);
        Assert.True(e.Query("app([1], [2], [1,2]).").Success);

        WasmTierDelegate.ResetDiag();
        Assert.True(e.Query(
            "nrev([1,2,3,4,5,6,7,8,9,10], R), R = [10|_].").Success);
        Assert.Equal(0, WasmTierDelegate.DiagSwitches);
        Assert.Equal(0, WasmTierDelegate.DiagBuiltins);
        // nrev's non-tail self-recursion and its tail handoff to app both
        // stay inside: the whole nrev(10) is a couple of chain entries (the
        // query's goals), not one per call.
        Assert.InRange(WasmTierDelegate.DiagEntries, 1, 4);
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

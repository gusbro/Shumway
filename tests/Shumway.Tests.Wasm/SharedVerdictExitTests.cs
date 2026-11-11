using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A partition's verdicts leave through one epilogue that spills the
/// machine registers into the mailbox (<see
/// cref="WasmPredicateCompiler.SharedExit"/>). Every deopt, builtin request
/// and wake request used to spill them in place, a third of the code a module
/// carried. The spill ends by writing the module id, so the stores to that
/// slot count the spills.</summary>
public sealed class SharedVerdictExitTests : IDisposable
{
    private readonly bool _saved = WasmPredicateCompiler.SharedExit;

    public void Dispose() => WasmPredicateCompiler.SharedExit = _saved;

    private const string Corpus = """
        sx_tak(X, Y, Z, A) :- X =< Y, !, Z = A.
        sx_tak(X, Y, Z, A) :-
            X1 is X - 1, sx_tak(X1, Y, Z, A1),
            Y1 is Y - 1, sx_tak(Y1, Z, X, A2),
            Z1 is Z - 1, sx_tak(Z1, X, Y, A3),
            sx_tak(A1, A2, A3, A).
        sx_app([], L, L).
        sx_app([H|T], L, [H|R]) :- sx_app(T, L, R).
        sx_rev([], []).
        sx_rev([H|T], R) :- sx_rev(T, RT), sx_app(RT, [H], R).
        % atom_length/2 has no form in the module: a builtin request each.
        sx_len([], 0).
        sx_len([X|Xs], N) :- atom_length(X, L), sx_len(Xs, N0), N is N0 + L.
        sx_run(A, R, N) :-
            sx_tak(18, 12, 6, A), numlist(1, 30, L), sx_rev(L, R),
            sx_len([ab, abc, hello, x], N).
        """;

    [Fact]
    public void APartitionSpillsTheRegistersOnce()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        engine.Query("true.");
        var env = new EngineWasmCompileEnv();
        var store = engine.IlPromotion;
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            if ((AtomTable.GetById(aid)?.Name ?? "").Contains("sx_"))
                members.Add(new WasmGroupMember(pred, addr,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId),
                    store.ImagePoolProvider?.Invoke(pred.FunctorId)));
        }
        Assert.True(members.Count >= 2, $"{members.Count} members");

        WasmPredicateCompiler.SharedExit = false;
        byte[] inPlace = WasmPredicateCompiler.CompileGroup(members, env, cps: false).Module;
        WasmPredicateCompiler.SharedExit = true;
        byte[] shared = WasmPredicateCompiler.CompileGroup(members, env, cps: false).Module;

        int inPlaceSpills = Spills(inPlace), sharedSpills = Spills(shared);
        // ANTI-VACUITY: the group has verdicts to share.
        Assert.True(inPlaceSpills >= 10, $"{inPlaceSpills} spills in place");
        Assert.True(sharedSpills * 4 < inPlaceSpills,
            $"{sharedSpills} spills shared against {inPlaceSpills} in place");
        Assert.True(shared.Length < inPlace.Length, $"{shared.Length} bytes against {inPlace.Length}");
    }

    [Fact]
    public void TheVerdictsReachTheHostAsBefore()
    {
        const string goal = "sx_run(A, R, N).";
        var tier0 = new PrologEngine();
        tier0.IlPromotion.Threshold = 0;
        tier0.ConsultString(Corpus);
        string expected = Answer(tier0.Query(goal));
        Assert.NotEqual("fail", expected);

        WasmPredicateCompiler.SharedExit = true;
        var (engine, members) = TieredEngine.Build(Corpus);
        Assert.True(engine.Query(goal).Success, goal);      // warm: promote
        // ANTI-VACUITY: the member that asks the host is on the tier.
        Assert.Contains(members, m =>
            (AtomTable.GetById(FunctorTable.Lookup(m.Predicate.FunctorId).AtomId)?.Name ?? "").Contains("sx_len"));
        Assert.Equal(expected, Answer(engine.Query(goal)));
    }

    private static string Answer(Solution r)
        => r.Success ? string.Join(",", r.Bindings.Select(b => $"{b.Key}={b.Value}")) : "fail";

    private static int Spills(byte[] bytes)
    {
        var module = WebAssembly.Module.ReadFromBinary(new MemoryStream(bytes));
        uint moduleIdSlot = WasmAbi.ByteOffset(WasmAbi.CurrentModuleId);
        int n = 0;
        foreach (var body in module.Codes)
            foreach (var ins in body.Code)
                if (ins is WebAssembly.Instructions.Int64Store s && s.Offset == moduleIdSlot) n++;
        return n;
    }
}

using System.Linq;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Shumway.Embedding.Debugging;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>An Immediate-window evaluation runs on Tier-0 as far as the wasm
/// tier goes: it neither enters a predicate the tier already covers nor
/// promotes one. The browser evaluates on a pool thread while the stop holds
/// the engine thread, and a module entered or promoted there waited on that
/// thread until the evaluation timed out -- which is how "evaluate binds a
/// copy" failed: a goal with a named variable runs the residual projection,
/// copy_term/3, which is prelude code the tier covers. IL keeps ADR-035's
/// per-predicate rule (Just My Code): only wasm stands aside.</summary>
[Collection("debugger")]
public sealed class DebugEvaluationStaysOnTier0Tests
{
    //   1: :- disable_debug.
    //   2: hot(X) :- X > 0.
    //   3: cold(X) :- X > 0.
    //   4: :- enable_debug.
    //   5: p(X) :-
    //   6:     q(X, Y),
    //   7:     r(Y).
    //   8: q(N, out(N)).
    //   9: r(_).
    private const string Program =
        ":- disable_debug.\nhot(X) :- X > 0.\ncold(X) :- X > 0.\n:- enable_debug.\n"
        + "p(X) :-\n    q(X, Y),\n    r(Y).\nq(N, out(N)).\nr(_).\n";

    private sealed record Outcome(string Answer, bool HotWasPromoted,
        bool ColdPromotedByTheEval, long EntriesDuringEval);

    private static Outcome Run()
    {
        var (e, members, _) = TieredEngine.BuildWithWorld(
            ":- set_prolog_flag(compile_mode, debug).\n" + Program, wasmThreshold: 1);
        e.QueryAll("set_prolog_flag(debug_lco, off).").ToList();
        // user's predicates are registered under their module: user$hot/1.
        static bool Is(WasmGroupMember m, string name)
        {
            var (atom, _) = FunctorTable.Lookup(m.Predicate.FunctorId);
            string n = AtomTable.GetById(atom)?.Name ?? "";
            return n == name || n.EndsWith("$" + name);
        }
        Assert.True(e.Query("hot(3).").Success);
        bool hotPromoted = members.Any(m => Is(m, "hot"));

        e.AddBreakpoint("<string>", 7);
        string answer = "";
        long entries = 0;
        bool evaluated = false;
        var svc = new DebugService(e, (s, ev) =>
        {
            if (evaluated) return;
            evaluated = true;
            long before = WasmTierDelegate.DiagEntries;
            // A named variable: the evaluation runs the residual projection too.
            answer = s.EvaluateGoal(0, "hot(3), cold(2), Z = 1.");
            entries = WasmTierDelegate.DiagEntries - before;
        });
        e.AttachDebugSession(svc);
        e.QueryAll("p(7).").ToList();
        e.AttachDebugSession(null);
        return new Outcome(answer, hotPromoted,
            members.Any(m => Is(m, "cold")), entries);
    }

    [Fact]
    public void AnEvaluationNeitherPromotesNorBreaksOnTheTier()
    {
        var r = Run();
        Assert.True(r.HotWasPromoted, "hot/1 never reached the tier: the test proves nothing");
        Assert.Equal("Z = 1", r.Answer);
        Assert.False(r.ColdPromotedByTheEval, "the evaluation promoted cold/1 to wasm");
    }

    [DiagFact]
    public void AnEvaluationDoesNotEnterTheTier()
        => Assert.Equal(0, Run().EntriesDuringEval);
}

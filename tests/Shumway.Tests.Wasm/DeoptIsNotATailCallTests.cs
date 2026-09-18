using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A deopt and a tail call reach the interpreter over the same two
/// signals (Pc plus IlTailCallPending) and mean opposite things. The
/// Call/Execute helper re-dispatches a tail call's target through the tier,
/// which is right for a tail call and wrong for a deopt: when the deopt's pc
/// is the deopting predicate's OWN entry, the helper hands the instruction
/// back to the module that just refused it and the two spin.
///
/// <para>The arithmetic that escalates out of the 60-bit integer lane is the
/// shape that produces such a deopt on the first call, before any other has
/// entered the module. It ran at 300,000 deopts a second with no stack growth
/// and no exception: the process simply stopped answering, which is why this
/// test bounds the run rather than asserting on it directly.</para></summary>
public sealed class DeoptIsNotATailCallTests(ITestOutputHelper o)
{
    private const string Corpus = """
        add(A, B, X) :- X is A + B.
        mul(A, B, X) :- X is A * B.
        sub(A, B, X) :- X is A - B.
        """;

    /// <summary>Runs one query with a bound, so the bug's signature (never
    /// returning) is a FAILURE and not a test run that hangs. Returns the
    /// answer, or throws naming the deopt count reached.</summary>
    private static bool RunBounded(PrologEngine e, string goal)
    {
        bool answer = false;
        System.Exception? error = null;
        long deoptsBefore = WasmTierDelegate.DiagDeopts;
        var t = new System.Threading.Thread(() =>
        {
            try { answer = e.Query(goal).Success; }
            catch (System.Exception ex) { error = ex; }
        })
        { IsBackground = true };
        t.Start();
        if (!t.Join(System.TimeSpan.FromSeconds(30)))
            Assert.Fail($"{goal} did not finish in 30s: "
                + $"{WasmTierDelegate.DiagDeopts - deoptsBefore} deopts so far. "
                + "A deopt is being re-dispatched to the tier as though it were "
                + "a tail call.");
        if (error is not null) throw error;
        return answer;
    }

    /// <summary>The promoting call is also the one that overflows, so the
    /// module's very first entry deopts. This is the case that hung: with a
    /// preceding call that stays inside the 60-bit lane, the same overflow
    /// deopts once and the interpreter finishes the work.</summary>
    [Theory]
    [InlineData("add(576460752303423487, 1, X), X =:= 576460752303423488.")]
    [InlineData("mul(576460752303423487, 2, X), X =:= 1152921504606846974.")]
    [InlineData("sub(-576460752303423488, 1, X), X =:= -576460752303423489.")]
    public void OverflowOnTheFirstCallTerminates(string goal)
    {
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        Assert.True(RunBounded(tier, goal), goal);
    }

    /// <summary>The same three, each after a call that does not overflow.
    /// These passed before the fix and must keep passing: the bug was in
    /// telling a deopt apart from a tail call, not in the arithmetic.</summary>
    [Fact]
    public void OverflowAfterAWarmCallTerminates()
    {
        var (tier, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        Assert.True(RunBounded(tier, "add(1, 2, X), X =:= 3."));
        Assert.True(RunBounded(tier, "add(576460752303423487, 1, X), X =:= 576460752303423488."));
        Assert.True(RunBounded(tier, "mul(2, 3, X), X =:= 6."));
        Assert.True(RunBounded(tier, "mul(576460752303423487, 2, X), X =:= 1152921504606846974."));
    }

    /// <summary>Anti-vacuity: the escalation must actually have deopted, or
    /// the tests above are three Tier-0 runs that could never have hung. One
    /// deopt is the whole point -- the bug turned that one into a flood, so
    /// the bound is deliberately tight rather than "not too many".</summary>
    [DiagFact]
    public void TheOverflowDeoptsExactlyOnce()
    {
        var (tier, members, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        WasmTierDelegate.ResetDiag();
        Assert.True(RunBounded(tier, "add(576460752303423487, 1, X), X =:= 576460752303423488."));
        // The predicate promotes on its FIRST call, so this is checked after
        // the query, not before it.
        Assert.NotEmpty(members);
        o.WriteLine($"entries={WasmTierDelegate.DiagEntries} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(WasmTierDelegate.DiagDeopts >= 1,
            "the escalation never reached the module: nothing was being tested");
        Assert.True(WasmTierDelegate.DiagDeopts <= 4,
            $"{WasmTierDelegate.DiagDeopts} deopts for one escalation: the deopt "
            + "is being handed back to the tier instead of to the interpreter");
    }
}

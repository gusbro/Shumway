using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 16 chunk 182: IL non-tail Call no longer creates a recursive
/// C# stack frame. The IL caller sets Cp to a resume marker, sets Pc
/// to the callee, sets IlTailCallPending = true, and *returns* to the
/// outer Dispatch loop. When the callee Proceeds, Pc lands on the
/// marker and the bytecode interpreter re-invokes the caller delegate
/// at the matching forward-resume cursor — no `RunSubroutine`
/// recursion, no chunk-174 floor pin needed for correctness.
///
/// <para>The architectural test: a deep chain of non-tail Calls
/// between Tier-1-promoted predicates stays in O(1) C# stack regardless
/// of Prolog call depth. Pre-threading, every Prolog call grew the C#
/// stack by RunSubroutine's frame plus Dispatch's recursive frame —
/// 10k Prolog calls would blow C#'s 1MB stack.</para>
/// </summary>
public class Chunk182Tests
{
    [Fact]
    public void DeepCallChain_DoesNotBlowCSharpStack_UnderTier1()
    {
        // A chain: count(0) :- !. count(N) :- N > 0, N1 is N - 1, count(N1).
        // Each recursive count(N) is a non-tail Call (because the goal
        // sequence has a tail Execute on count(N1) — wait, it IS tail).
        // Actually that compiles to Execute. To force non-tail Call we
        // need work *after* the recursion:
        //   chain(0, Acc, Acc) :- !.
        //   chain(N, Acc, Out) :- N > 0, N1 is N - 1, chain(N1, Acc, Mid), Out = Mid.
        // The `Out = Mid` after the recursive call makes chain/3 a
        // non-tail Call to itself.
        var engine = new PrologEngine();
        // Force every predicate to promote on first call.
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            chain(0, Acc, Acc) :- !.
            chain(N, Acc, Out) :- N > 0, N1 is N - 1,   chain(N1, Acc, Mid), Out = Mid.
            """);

        // 5000 deep — pre-threading this overflowed at ~1500 nested
        // C# RunSubroutine frames.
        var sol = engine.Query("chain(5000, ok, X).");
        Assert.True(sol.Success);
        Assert.Equal("ok", sol.Bindings["X"].ToString());
    }

    [Fact]
    public void BacktrackAcrossThreadedCallBoundary_FindsAllSolutions()
    {
        // Multi-clause callee backtracks through several alternatives,
        // each visible to a non-tail Call'd from a Tier-1 caller.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString("""
            color(red). color(green). color(blue).
            wrap(X, wrapped(X)).
            test(L) :- findall(W, (color(C), wrap(C, W)), L).
            """);

        var sol = engine.Query("test(L).");
        Assert.True(sol.Success);
        string list = sol.Bindings["L"].ToString()!;
        Assert.Contains("wrapped(red)", list);
        Assert.Contains("wrapped(green)", list);
        Assert.Contains("wrapped(blue)", list);
    }

    [Fact]
    public void Tier1WithThreshold32_StillCorrect_NoFloorPinRequired()
    {
        // The mixed scenario that surfaced the chunk-174 Y-slot bug:
        // a Tier-1 caller, a Tier-0 callee with multi-clause
        // backtracking. Threading must keep this correct without the
        // RunSubroutine floor pin.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 32;
        engine.ConsultString("""
            step(a, 1). step(b, 2). step(c, 3).
            look_up(K, V) :- step(K, V), !.
            loop(0, []).
            loop(N, [K-V|T]) :- N > 0, N1 is N - 1, 
              ( N mod 3 =:= 0 -> K = a ; N mod 3 =:= 1 -> K = b ; K = c ),
              look_up(K, V), loop(N1, T).
            """);

        // Force loop to warm past the threshold.
        var sol = engine.Query("loop(100, L), length(L, N).");
        Assert.True(sol.Success);
        Assert.Equal("100", sol.Bindings["N"].ToString());
    }
}

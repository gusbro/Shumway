using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A catch/3 that nothing can come back to costs nothing once it is
/// over, in a deterministic loop of any length.
///
/// <para>A frame used to go only when its push record unwound, and a
/// deterministic loop never unwinds. A frame left on a deterministic exit was
/// already given back; two cases still accumulated. A binding the guarded goal
/// made to an older cell was trailed because the frame raised the heap
/// boundary, and the entry stayed after the frame was gone: 8 bytes a call. A
/// goal that left a choice point closed its frame with the choice point alive,
/// a cut after the catch removed the choice point, and the closed frame and its
/// two records stayed: 16 bytes of trail and a frame a call. The second also
/// made every compacting cut walk all the frames, quadratic in a nesting of
/// such catches.</para></summary>
public sealed class CatchFrameLifetimeTests
{
    // The loops end in an if-then-else, not in a clause with a cut: that cut
    // compacts the trail on the way out and hides what the loop left.
    private const string Program = """
        p1(N) :- ( N =:= 0 -> true ; catch(true, _, true), M is N-1, p1(M) ).
        p2(N) :- ( N =:= 0 -> true ; X = f(_), catch(X = f(a), _, true), M is N-1, p2(M) ).
        p3(N) :- ( N =:= 0 -> true ; catch(member(_, [a,b]), _, true), !, M is N-1, p3(M) ).
        p4(N) :- ( N =:= 0 -> true ; catch(throw(x), x, true), M is N-1, p4(M) ).
        p5(N) :- ( N =:= 0 -> true ; catch(catch(true, _, true), _, true), M is N-1, p5(M) ).
        p7(N) :- ( N =:= 0 -> true ; catch(R = ok, R, true), M is N-1, p7(M) ).

        st(T, C) :- statistics(trail_stack, [T, _]), statistics(cstr_stack, [C, _]).
        grow(P, N, DT, DC) :-
            G =.. [P, N], st(T0, C0), call(G), st(T1, C1),
            DT is T1 - T0, DC is C1 - C0.

        deepd(0) :- !.
        deepd(N) :- M is N-1, catch((member(_, [a,b]), deepd(M)), -, true), !.

        caught(C, R) :- catch(thrower(C, R), oops, R = caught).
        thrower(C, R) :- setup_call_cleanup(true, member(_, [1,2]), C), !, R = not_thrown.
        """;

    private static PrologEngine Engine(bool compiled)
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        if (compiled) e.IlPromotion.Threshold = 1;
        return e;
    }

    // Bytes of trail and of extra trail a run of N leaves, at N and at 2N:
    // a cost per call shows as the second being about twice the first.
    private static (long T1, long C1, long T2, long C2) Growth(PrologEngine e, string p)
    {
        e.Query($"{p}(100).");   // compiled, when it is to be
        var a = e.Query($"grow({p}, 20000, T, C).");
        var b = e.Query($"grow({p}, 40000, T, C).");
        Assert.True(a.Success && b.Success, p);
        long v(Solution s, string k) => long.Parse(s[k]!.ToString()!);
        return (v(a, "T"), v(a, "C"), v(b, "T"), v(b, "C"));
    }

    [Theory]
    [InlineData("p1", false)] [InlineData("p1", true)]
    [InlineData("p2", false)] [InlineData("p2", true)]
    [InlineData("p3", false)] [InlineData("p3", true)]
    [InlineData("p4", false)] [InlineData("p4", true)]
    [InlineData("p5", false)] [InlineData("p5", true)]
    [InlineData("p7", false)] [InlineData("p7", true)]
    public void ALoopOfCatches_LeavesNoTrailBehind(string p, bool compiled)
    {
        var (t1, c1, t2, c2) = Growth(Engine(compiled), p);
        // 40,000 calls at 8 bytes a call would be 320,000. A constant of a
        // few entries (the last frame's records) is what may stay.
        Assert.True(t2 <= 64 && c2 <= 64, $"{p}: trail {t1} then {t2}, extra trail {c1} then {c2}");
    }

    [Fact]
    public void TheMeasureSeesACostPerCall()
    {
        // ANTI-VACUITY: a loop that does keep something a call shows on the
        // same two numbers.
        var e = new PrologEngine();
        e.ConsultString("""
            keep(0, _) :- !.
            keep(N, L) :- b_setval(k, N), M is N-1, keep(M, L).
            st(C) :- statistics(cstr_stack, [C, _]).
            """);
        var s = e.Query("st(C0), keep(5000, _), st(C1), D is C1 - C0.");
        Assert.True(s.Success);
        Assert.True(long.Parse(s["D"]!.ToString()!) >= 5000 * 8);
    }

    // What the frames that are given back used to keep must not be what the
    // machine needs. A binding inside a catch to a cell older than a choice
    // point still live is undone by backtracking to it, and so is a binding
    // after the catch, which the restored boundary still trails.
    [Theory]
    [InlineData("X = f(_), member(Y, [1,2]), catch(X = f(Y), _, true), Y == 2, X == f(2)")]
    [InlineData("X = f(_), member(Y, [1,2]), catch(true, _, true), X = f(Y), Y == 2, X == f(2)")]
    [InlineData("X = f(_), member(Y, [1,2]), ( catch(member(_, [a,b]), _, true) -> true ; true ), catch(true, _, true), X = f(Y), Y == 2, X == f(2)")]
    public void BacktrackingUndoesWhatItMust(string goal)
    {
        foreach (bool compiled in new[] { false, true })
            Assert.True(Engine(compiled).Query(goal + ".").Success, $"{goal} (compiled: {compiled})");
    }

    // A closed frame whose goal still has a choice point is not given back:
    // backtracking into the goal opens it again, and it catches.
    [Fact]
    public void AClosedFrameAChoicePointCanReachStaysAndCatches()
    {
        foreach (bool compiled in new[] { false, true })
        {
            var s = Engine(compiled).Query(
                "catch((member(X, [1,2]), (X == 2 -> throw(t) ; true)), t, X = caught), "
                + "catch(true, _, true), X == caught.");
            Assert.True(s.Success, $"compiled: {compiled}");
        }
    }

    // The cut in thrower/2 leaves setup_call_cleanup's frame closed with
    // nothing to come back to, and runs the cleanup in a driver of its own
    // that counts that frame. A catch in that driver must not give it back:
    // its own frame would take the index, a ball for it would be caught by
    // the wrong driver, and the query would go on from the wrong place. A
    // cleanup that throws reaches the drain's catch, and its ball then
    // reaches caught/2; one that catches its own ball brings its own.
    [Theory]
    [InlineData("throw(oops)", "caught")]
    [InlineData("catch(throw(oops), oops, true)", "not_thrown")]
    public void ACleanupThatThrowsAtACut_GoesOnAfterTheCatch(string cleanup, string r)
    {
        foreach (bool compiled in new[] { false, true })
        {
            var e = Engine(compiled);
            for (int i = 0; i < 3; i++)
            {
                var s = e.Query($"caught(({cleanup}), R), X = reached.");
                string where = $"compiled: {compiled}, run {i}";
                Assert.True(s.Success, where);
                Assert.True(s["R"]!.ToString() == r, $"{where}: R = {s["R"]}");
                Assert.True(s["X"]!.ToString() == "reached", $"{where}: X = {s["X"]}");
            }
        }
    }

    // Nested catches whose goals leave a choice point that a cut after each
    // catch removes. Every compacting cut walked all the frames: 36 s for
    // 50,000 levels and 193 s for 100,000 before, 0.15 s and 0.19 s after (a
    // Release build). The bound below is two orders of magnitude off either.
    [Fact]
    public void NestedCatchesThatACutCloses_StayLinear()
    {
        var e = Engine(compiled: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(e.Query("deepd(40000).").Success);
        Assert.True(sw.Elapsed.TotalSeconds < 8, $"deepd(40000) took {sw.Elapsed.TotalSeconds:F1} s");
    }
}

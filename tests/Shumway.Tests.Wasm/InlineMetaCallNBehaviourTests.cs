using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>call/N for N &gt;= 2 dispatched inside the module.
///
/// <para>The emitter question and the cache key are pinned elsewhere. What
/// is pinned HERE is the thing that can be subtly wrong while still
/// answering: the appended arguments have to land AFTER the goal's own
/// ones, in order, and the goal's own arguments are copied over the very
/// registers the appended ones arrive in. A copy that ran before the park
/// would answer correctly whenever the goal's arity happens to be 0, and
/// wrongly otherwise.</para>
///
/// <para>Every test that claims the inline path asserts zero deopts, since
/// stepping aside makes the answers correct by construction and would
/// prove only that the interpreter works.</para></summary>
public sealed class InlineMetaCallNBehaviourTests(ITestOutputHelper o)
{
    private const string Corpus = """
        got1(A, R) :- R = g(A).
        got2(A, B, R) :- R = g(A, B).
        got3(A, B, C, R) :- R = g(A, B, C).
        wide(A, B, C, D, E, F, G, H) :- H = w(A, B, C, D, E, F, G).
        two(G, X, R) :- call(G, X, R).
        three(G, X, Y, R) :- call(G, X, Y, R).
        apply1(G, X) :- call(G, X).
        """;

    private static PrologEngine Plain(string program)
    {
        var e = new PrologEngine();
        e.ConsultString(program);
        return e;
    }

    /// <summary>Both halves of the argument list, in order. got2 REPORTS
    /// what it received, so an appended argument that landed on top of a
    /// goal argument (or the other way round) shows up as a different term
    /// rather than as a failure.</summary>
    [DiagFact]
    public void TheAppendedArgumentsFollowTheGoalsOwn()
    {
        const string Goal = "two(got2(a), b, R), R == g(a, b).";
        Assert.True(Plain(Corpus).Query(Goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(Goal).Success);          // warm the cache
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(Goal).Success,
            "call(G, X, R) did not place the arguments in order");
        o.WriteLine($"two: deopts={WasmTierDelegate.DiagDeopts}");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>The goal already carries more arguments than the call site
    /// appends, so the parked registers are written back PAST where they
    /// sat. This is the direction a copy-then-place order gets wrong.
    /// </summary>
    [DiagFact]
    public void AWideGoalWithFewAppendedArguments()
    {
        const string Goal =
            "apply1(wide(1, 2, 3, 4, 5, 6, 7), R), R == w(1, 2, 3, 4, 5, 6, 7).";
        Assert.True(Plain(Corpus).Query(Goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(Goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(Goal).Success,
            "the widest goal lost an argument");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>Appending more than one, which is where an off-by-one in
    /// the write-back address would land two arguments on one slot.
    /// </summary>
    [DiagFact]
    public void TwoAppendedArgumentsKeepTheirOrder()
    {
        const string Goal = "three(got3(a), b, c, R), R == g(a, b, c).";
        Assert.True(Plain(Corpus).Query(Goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(Goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(Goal).Success,
            "call(G, X, Y, R) crossed or dropped an appended argument");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>The same goal reached by call/2 and by call/3 resolves to
    /// DIFFERENT predicates. One cache key for both would serve the first
    /// answer to the second, which is a wrong call and not a slow one.
    /// </summary>
    [Fact]
    public void TheSameGoalAtTwoWidthsResolvesSeparately()
    {
        const string Goal =
            "apply1(got1(a), R1), two(got2(a), b, R2), "
            + "R1 == g(a), R2 == g(a, b).";
        Assert.True(Plain(Corpus).Query(Goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(Goal).Success);
        Assert.True(tiered.Query(Goal).Success,
            "call/2 and call/3 of the same goal shared a resolution");
    }

    /// <summary>An arity the module cannot serve must still ANSWER. Past
    /// the register window the width guard sends it to the host, and the
    /// only way to tell that apart from a bug is that the answer is right.
    /// </summary>
    [Fact]
    public void AWidthPastTheWindowStillAnswers()
    {
        const string Program = Corpus + """

            huge(A, B, C, D, E, F, G, H, I) :- I = h(A, B, C, D, E, F, G, H).
            over(G, X) :- call(G, X).
            """;
        const string Goal =
            "over(huge(1, 2, 3, 4, 5, 6, 7, 8), R), R == h(1, 2, 3, 4, 5, 6, 7, 8).";
        Assert.True(Plain(Program).Query(Goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query(Goal).Success,
            "a call/N too wide for the module must still answer");
    }

    /// <summary>Backtracking through an appending meta-call: the goal
    /// leaves choice points and the redo has to find the appended
    /// arguments where they were put, not where they arrived.</summary>
    [DiagFact]
    public void BacktrackingThroughAnAppendingMetaCall()
    {
        const string Program = """
            pick(k, a, 1).
            pick(k, b, 2).
            pick(k, c, 3).
            two(G, X, R) :- call(G, X, R).
            drive(L) :- findall(X-N, two(pick(k), X, N), L).
            """;
        Assert.True(Plain(Program).Query(
            "drive(L), L == [a-1, b-2, c-3].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("drive(_).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("drive(L), L == [a-1, b-2, c-3].").Success,
            "backtracking through call/3 lost solutions");
        o.WriteLine($"redo: deopts={WasmTierDelegate.DiagDeopts}");
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>The motivating case, stated as a test: maplist/3 IS
    /// call(G, X, Y) in a loop, and every element used to deopt. The count
    /// is what the arc is about, so it is the assertion.</summary>
    [DiagFact]
    public void MaplistDoesNotDeoptPerElement()
    {
        const string Program = """
            times(K, X, Y) :- Y is X * K.
            mapl(_, [], []).
            mapl(G, [X|Xs], [Y|Ys]) :- call(G, X, Y), mapl(G, Xs, Ys).
            drive(N, S) :-
                numlist(1, N, L), mapl(times(2), L, D), sum_list(D, S).
            """;
        Assert.True(Plain(Program).Query("drive(200, S), S == 40200.").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("drive(200, _).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("drive(200, S), S == 40200.").Success,
            "the mapping answer moved on the tier");
        o.WriteLine($"maplist 200: deopts={WasmTierDelegate.DiagDeopts}");
        // 200 elements used to be 200 deopts. Zero is the claim; a handful
        // would mean the cache is missing, which is the same bug slower.
        Assert.Equal(0L, WasmTierDelegate.DiagDeopts);
    }

    /// <summary>A cut inside the goal cuts the GOAL: call/N is opaque to
    /// cut exactly as call/1 is. The barrier is refreshed on the appending
    /// path too, or the cut would prune the caller.</summary>
    [Fact]
    public void ACutInsideAnAppendedGoalIsOpaque()
    {
        const string Program = """
            cutty(X, Y) :- Y = X, !.
            cutty(_, other).
            caller(X, Y) :- call(cutty, X, Y).
            caller(_, second).
            drive(L) :- findall(Y, caller(a, Y), L).
            """;
        Assert.True(Plain(Program).Query("drive(L), L == [a, second].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("drive(_).").Success);
        Assert.True(tiered.Query("drive(L), L == [a, second].").Success,
            "a cut inside an appended goal escaped its barrier");
    }

    /// <summary>The shapes it declines still answer the way the builtin
    /// does: an unbound goal raises, a nonexistent predicate raises, and an
    /// ATOM goal (nothing to read off the heap) runs.</summary>
    [Fact]
    public void TheShapesItDeclinesStillAnswer()
    {
        var (t1, _) = TieredEngine.Build(Corpus);
        Assert.True(t1.Query("catch(two(_, b, _), E, true), nonvar(E).").Success,
            "an unbound goal must raise");

        var (t2, _) = TieredEngine.Build(Corpus);
        Assert.False(t2.Query("catch(two(nosuch_here, b, _), _, fail).").Success);

        var (t3, _) = TieredEngine.Build(Corpus);
        Assert.True(t3.Query("two(got1, a, R), R == g(a).").Success,
            "an atom goal must still run");
    }

    /// <summary>A goal that is a bare ATOM at run time is NOT served here,
    /// and this pins that on purpose rather than by omission.
    ///
    /// <para>The module resolves an atom goal through a table keyed by atom
    /// that names the name/0 predicate. An appending call site wants
    /// name/appended instead, and the module cannot form that functor id:
    /// interning is a SEARCH of the functor table and a module can only
    /// index. So it steps aside, correctly and slowly.</para>
    ///
    /// <para>It matters less than it reads. A call/N whose goal is a
    /// literal atom is rewritten into a direct call before the tier sees
    /// it, so this is reached only by a goal that arrives in a variable.
    /// If someone teaches the module the atom form, this test goes red and
    /// should be turned into the opposite claim.</para></summary>
    [DiagFact]
    public void AnAtomGoalStillStepsAsideAndStillAnswers()
    {
        const string Program = """
            pick(a, 1).
            pick(b, 2).
            two(G, X, R) :- call(G, X, R).
            drive(G, L) :- findall(X-N, two(G, X, N), L).
            """;
        Assert.True(Plain(Program).Query(
            "drive(pick, L), L == [a-1, b-2].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("drive(pick, _).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("drive(pick, L), L == [a-1, b-2].").Success,
            "the atom form of call/3 stopped answering");
        o.WriteLine($"atom goal: deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(WasmTierDelegate.DiagDeopts > 0,
            "the atom form no longer steps aside: if that is deliberate, "
            + "this test is the one that has to change");
    }
}

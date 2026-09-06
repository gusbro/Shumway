using Shumway.Core;

namespace Shumway.Tests.Wasm;

/// <summary>Cut and builtins in the wasm backend. Cut is B moving down to a
/// barrier: neck_cut against the entry barrier the driver stages, deep cut
/// against a Y-captured level (allocate_get_level / cut_deallocate_proceed),
/// and the inline if-then-else that ADR-025 lowers to a $disj helper with a
/// sentinel-arity try_me_else. A builtin leaves the module through
/// <c>BuiltinRequest</c> -- its id and the return cursor in the mailbox --
/// and the host runs it and re-enters; in tail position the cursor is the
/// proceed sentinel.</summary>
public class WasmCutBuiltinTests
{
    private const string Corpus = """
        max(X, Y, X) :- X >= Y, !.
        max(_, Y, Y).

        first([X|_], X) :- !.

        tv(X) :- var(X), !.
        ti(X) :- integer(X).

        om(X, L) :- mem(X, L), !.
        mem(X, [X|_]).
        mem(X, [_|T]) :- mem(X, T).

        classify(X, neg) :- X < 0, !.
        classify(0, zero) :- !.
        classify(_, pos).

        sign(X, R) :- ( X > 0 -> R = pos ; R = neg ).
        """;

    private static WasmProgramHarness Harness() => new(Corpus);

    [Theory]
    [InlineData(3, 5, 5)]
    [InlineData(5, 3, 5)]
    [InlineData(4, 4, 4)]
    public void MaxCommitsToItsFirstAnswer(long x, long y, long expected)
    {
        using var h = Harness();
        Assert.True(h.Solve("max", x, y, null));
        Assert.Equal(expected, h.Answer(2).AsInt);
        // The cut killed the trust_me alternative: no second answer, ever.
        Assert.False(h.NextSolution());
    }

    [Fact]
    public void WithoutTheCutThereWouldBeTwo()
    {
        // The control: max(5,3) matched clause 1; without the neck_cut the
        // second clause would offer 3 as another answer. It does not.
        using var h = Harness();
        Assert.True(h.Solve("max", 5, 3, null));
        Assert.Equal(5, h.Answer(2).AsInt);
        Assert.False(h.NextSolution());
    }

    [Fact]
    public void ANeckCutAfterHeadMatching()
    {
        using var h = Harness();
        h.Fresh();
        Assert.True(h.SolveWith("first", h.MakeIntList(7, 8, 9), null));
        Assert.Equal(7, h.Answer(1).AsInt);
        Assert.False(h.NextSolution());
    }

    [Fact]
    public void DeepCutPrunesTheCalleesChoicePoints()
    {
        // om/2: the cut after mem(X, L) discards mem's own choice points --
        // allocate_get_level captured the barrier, cut_deallocate_proceed
        // commits to it. One answer where mem alone had three.
        using var h = Harness();
        h.Fresh();
        Assert.True(h.SolveWith("om", null, h.MakeIntList(1, 2, 3)));
        Assert.Equal("1", h.Render(h.Answer(0)));
        Assert.False(h.NextSolution());

        // ...and mem itself still enumerates, so the pruning was the cut's.
        h.Fresh();
        Assert.True(h.SolveWith("mem", null, h.MakeIntList(1, 2, 3)));
        int count = 1;
        while (h.NextSolution()) count++;
        Assert.Equal(3, count);
    }

    [Fact]
    public void ABuiltinMidBodySucceedsAndFails()
    {
        // tv/1: var(X) through BuiltinRequest, then the cut. With a fresh
        // variable it holds; with an integer the builtin fails and the
        // failure backtracks out of the module.
        using var h = Harness();
        Assert.True(h.Solve("tv", (long?)null));
        Assert.False(h.Solve("tv", 5));
    }

    [Fact]
    public void ABuiltinInTailPositionProceeds()
    {
        // ti/1 is nothing but `execute integer/1`: the request carries the
        // proceed sentinel, and the driver continues at the continuation.
        using var h = Harness();
        Assert.True(h.Solve("ti", 42));
        Assert.False(h.Solve("ti", (long?)null));
    }

    [Theory]
    [InlineData(-3, "neg")]
    [InlineData(0, "zero")]
    [InlineData(7, "pos")]
    public void GuardedClausesCommit(long x, string expected)
    {
        using var h = Harness();
        Assert.True(h.Solve("classify", x, null));
        Assert.Equal(expected, h.Render(h.Answer(1)));
        Assert.False(h.NextSolution());
    }

    [Theory]
    [InlineData(5, "pos")]
    [InlineData(-5, "neg")]
    [InlineData(0, "neg")]
    public void InlineIfThenElse(long x, string expected)
    {
        // sign/2 lowers to a $disj helper: try_me_else with the ADR-025
        // sentinel arity, neck_cut committing the condition, =/2 as a tail
        // builtin, trust_me for the else.
        using var h = Harness();
        Assert.True(h.Solve("sign", x, null));
        Assert.Equal(expected, h.Render(h.Answer(1)));
        Assert.False(h.NextSolution());
    }

    [Fact]
    public void TheEngineAgrees()
    {
        var engine = new Shumway.Embedding.PrologEngine();
        engine.ConsultString(Corpus);
        Assert.True(engine.Query("max(3, 5, 5), classify(-2, neg), sign(0, neg).").Success);
        Assert.True(engine.Query("findall(X, om(X, [1,2,3]), [1]).").Success);
        Assert.False(engine.Query("tv(5).").Success);
    }
}

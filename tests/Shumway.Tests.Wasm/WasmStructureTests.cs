using Shumway.Compiler.Wasm;
using Shumway.Core;

namespace Shumway.Tests.Wasm;

/// <summary>Structures and lists in the wasm backend: the ADR-017 inline
/// cells (a cons is two cells, a structure is functor plus args), the
/// read/write unify machine with its S pointer, and ADR-019's last-argument
/// nested builds. The programs are the classics because the classics cover
/// the machine: append destructures and builds at once, member enumerates
/// through a cons, nrev stacks frames on calls, and the deep/mk pair walks
/// nested structures both directions.</summary>
public class WasmStructureTests
{
    private const string Corpus = """
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).

        member(X, [X|_]).
        member(X, [_|T]) :- member(X, T).

        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).

        point(p(X, Y), X, Y).

        deep(f(g(h(X))), X).

        mk(N, p(N, q(N))).

        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        """;

    private static WasmProgramHarness Harness() => new(Corpus);

    [Fact]
    public void AppendBuildsTheAnswer()
    {
        using var h = Harness();
        h.Fresh();
        var xs = h.MakeIntList(1, 2);
        var ys = h.MakeIntList(3);
        Assert.True(h.SolveWith("app", xs, ys, null));
        Assert.Equal("[1,2,3]", h.Render(h.Answer(2)));
    }

    [Fact]
    public void AppendChecksToo()
    {
        // The whole SHARES its tail with ys, the way an append's output
        // usually does: the base case then compares one cell with itself.
        // (Two structurally equal but distinct lists reach a general
        // compound unify, which this slice deliberately steps aside on --
        // the interpreter finishes such a clause in the real integration.)
        using var h = Harness();
        h.Fresh();
        var ys = h.MakeIntList(3);
        var whole = h.MakePartialList([1, 2], ys);
        Assert.True(h.SolveWith("app", h.MakeIntList(1, 2), ys, whole));
        h.Fresh();
        Assert.False(h.SolveWith("app",
            h.MakeIntList(1, 2), h.MakeIntList(3), h.MakeIntList(9, 2, 4)));
    }

    [Fact]
    public void AppendEnumeratesItsSplits()
    {
        // app(X, Y, [1,2]): three answers, each built by WRITE mode into the
        // unbound arguments and unbuilt again by the trail on backtracking.
        using var h = Harness();
        h.Fresh();
        var whole = h.MakeIntList(1, 2);
        Assert.True(h.SolveWith("app", null, null, whole));
        var seen = new List<string> { $"{h.Render(h.Answer(0))} ++ {h.Render(h.Answer(1))}" };
        while (h.NextSolution())
            seen.Add($"{h.Render(h.Answer(0))} ++ {h.Render(h.Answer(1))}");
        Assert.Equal(new[]
        {
            "[] ++ [1,2]",
            "[1] ++ [2]",
            "[1,2] ++ []",
        }, seen);
    }

    [Fact]
    public void MemberFindsAndEnumerates()
    {
        using var h = Harness();
        h.Fresh();
        Assert.True(h.SolveWith("member", h.MakeIntNull(2), h.MakeIntList(1, 2, 3)));
        h.Fresh();
        Assert.False(h.SolveWith("member", h.MakeIntNull(9), h.MakeIntList(1, 2, 3)));

        h.Fresh();
        Assert.True(h.SolveWith("member", null, h.MakeIntList(1, 2, 3)));
        var seen = new List<string> { h.Render(h.Answer(0)) };
        while (h.NextSolution()) seen.Add(h.Render(h.Answer(0)));
        Assert.Equal(new[] { "1", "2", "3" }, seen);
    }

    [Theory]
    [InlineData(new long[] { 1, 2, 3 }, "[3,2,1]")]
    [InlineData(new long[] { 7 }, "[7]")]
    [InlineData(new long[] { }, "[]")]
    public void NaiveReverseThroughRealFrames(long[] input, string expected)
    {
        using var h = Harness();
        h.Fresh();
        Assert.True(h.SolveWith("nrev", h.MakeIntList(input), null));
        Assert.Equal(expected, h.Render(h.Answer(1)));
    }

    [Fact]
    public void AStructureReadsItsArguments()
    {
        using var h = Harness();
        h.Fresh();
        var p = h.MakeStruct("p", h.CellInt(3), h.CellInt(4));
        Assert.True(h.SolveWith("point", p, null, null));
        Assert.Equal("3", h.Render(h.Answer(1)));
        Assert.Equal("4", h.Render(h.Answer(2)));
    }

    [Fact]
    public void AStructureBuildsIntoAnUnboundArgument()
    {
        using var h = Harness();
        h.Fresh();
        Assert.True(h.SolveWith("point", null, h.CellInt(3), h.CellInt(4)));
        Assert.Equal("p(3,4)", h.Render(h.Answer(0)));
    }

    [Fact]
    public void NestedStructuresReadAndBuild()
    {
        using var h = Harness();
        h.Fresh();
        var term = h.MakeStruct("f", h.MakeStruct("g", h.MakeStruct("h", h.CellInt(7))));
        Assert.True(h.SolveWith("deep", term, null));
        Assert.Equal("7", h.Render(h.Answer(1)));

        h.Fresh();
        Assert.True(h.SolveWith("deep", null, h.CellInt(7)));
        Assert.Equal("f(g(h(7)))", h.Render(h.Answer(0)));
    }

    [Fact]
    public void TheLastArgumentNestedBuild()
    {
        // mk(5, M): p(5, q(5)) -- unify_structure in write mode, the ADR-019
        // shape, with the shared variable appearing on both sides of it.
        using var h = Harness();
        h.Fresh();
        Assert.True(h.SolveWith("mk", h.CellInt(5), null));
        Assert.Equal("p(5,q(5))", h.Render(h.Answer(1)));
    }

    [Fact]
    public void ListLengthCountsThroughUnifyVoid()
    {
        // member's first clause uses unify_void; len walks a list without
        // touching the elements at all.
        using var h = Harness();
        h.Fresh();
        Assert.True(h.SolveWith("len", h.MakeIntList(4, 5, 6, 7), null));
        Assert.Equal("4", h.Render(h.Answer(1)));
    }

    [Fact]
    public void TheAnswersMatchTheEngine()
    {
        // The differential half: the same queries on the real engine.
        var engine = new Shumway.Embedding.PrologEngine();
        engine.ConsultString(Corpus);
        Assert.True(engine.Query(
            "nrev([1,2,3,4,5,6], R), R == [6,5,4,3,2,1].").Success);

        using var h = Harness();
        h.Fresh();
        Assert.True(h.SolveWith("nrev", h.MakeIntList(1, 2, 3, 4, 5, 6), null));
        Assert.Equal("[6,5,4,3,2,1]", h.Render(h.Answer(1)));
    }
}

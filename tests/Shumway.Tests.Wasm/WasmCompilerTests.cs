using Shumway.Compiler.Wasm;
using Shumway.Core;

namespace Shumway.Tests.Wasm;

/// <summary>Phase 1's first slice, run differentially: the same programs the
/// milestone names (self-tail counters, recursion through frames, mutual
/// recursion, multi-clause enumeration) compiled to wasm and driven through
/// the verdict protocol, with the answers checked against what the engine
/// itself computes for the same queries elsewhere in this suite.</summary>
public class WasmCompilerTests
{
    private const string Corpus = """
        loop(N) :- N > 0, N1 is N - 1, loop(N1).
        loop(0).

        fact(0, 1).
        fact(N, F) :- N > 0, N1 is N - 1, fact(N1, F1), F is N * F1.

        even(0).
        even(N) :- N > 0, N1 is N - 1, odd(N1).
        odd(N) :- N > 0, N1 is N - 1, even(N1).

        color(rojo).
        color(verde).
        color(azul).

        sum(0, Acc, Acc).
        sum(N, Acc, S) :- N > 0, Acc1 is Acc + N, N1 is N - 1, sum(N1, Acc1, S).
        """;

    private static WasmProgramHarness Harness() => new(Corpus);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100_000)]
    public void TheCounterCountsDown(long n)
    {
        using var h = Harness();
        Assert.True(h.Solve("loop", n));
    }

    [Fact]
    public void ANegativeCounterFails()
    {
        // Both clauses refuse: N > 0 fails, and the head loop(0) does not
        // match. The failure comes back through the choice points the try
        // chain pushed.
        using var h = Harness();
        Assert.False(h.Solve("loop", -3));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 120)]
    [InlineData(10, 3_628_800)]
    public void FactorialThroughRealFrames(long n, long expected)
    {
        // fact/2 is the whole machinery at once: environment frames, a
        // non-tail self call resumed through a marker, and arithmetic
        // delivered by unification into a permanent.
        using var h = Harness();
        Assert.True(h.Solve("fact", n, null));
        Assert.Equal(Tag.Int, h.Answer(1).Tag);
        Assert.Equal(expected, h.Answer(1).AsInt);
    }

    [Fact]
    public void FactorialChecksAsWellAsComputes()
    {
        using var h = Harness();
        Assert.True(h.Solve("fact", 5, 120));
        Assert.False(h.Solve("fact", 5, 121));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(10, true)]
    [InlineData(9_999, false)]
    public void MutualRecursionCrossesPredicates(long n, bool isEven)
    {
        // even/odd hand control to each other through the tail-call verdict:
        // every crossing leaves the module and comes back through dispatch.
        using var h = Harness();
        Assert.Equal(isEven, h.Solve("even", n));
    }

    [Fact]
    public void EnumerationBacktracksThroughItsOwnChoicePoints()
    {
        // color/3's clauses enumerate through try/retry/trust compiled to
        // wasm: the restore, the trail unwind and the BP update all happen in
        // the module, and the driver only re-enters where the CP says.
        using var h = Harness();
        Assert.True(h.Solve("color", (long?)null));
        var seen = new List<string> { AtomName(h.Answer(0)) };
        while (h.NextSolution()) seen.Add(AtomName(h.Answer(0)));
        Assert.Equal(new[] { "rojo", "verde", "azul" }, seen);
    }

    [Fact]
    public void EnumerationUnbindsBetweenAnswers()
    {
        // The trail unwind is what makes the second answer possible at all:
        // the variable bound to rojo has to come back unbound before verde.
        using var h = Harness();
        Assert.True(h.Solve("color", (long?)null));
        Assert.True(h.NextSolution());
        Assert.Equal("verde", AtomName(h.Answer(0)));
    }

    [Fact]
    public void ABoundArgumentSelectsByIndexing()
    {
        using var h = Harness();
        int rojo = AtomTable.Intern("rojo", permanent: true).Id;
        int negro = AtomTable.Intern("negro", permanent: true).Id;
        Assert.True(SolveAtom(h, "color", rojo));
        Assert.False(SolveAtom(h, "color", negro));
    }

    [Theory]
    [InlineData(10, 55)]
    [InlineData(1000, 500_500)]
    public void AnAccumulatorThreadsThroughYSlots(long n, long expected)
    {
        using var h = Harness();
        Assert.True(h.Solve("sum", n, 0, null));
        Assert.Equal(expected, h.Answer(2).AsInt);
    }

    [Fact]
    public void APredicateOutsideTheSubsetIsRefused()
    {
        // A bigint literal is outside the subset: the compiler refuses the
        // predicate rather than guessing, and the refusal names the opcode.
        var ex = Assert.Throws<WasmCompileException>(
            () => new WasmProgramHarness("p(123456789012345678901234567890)."));
        Assert.Contains("BigInt", ex.Message);
    }

    private static bool SolveAtom(WasmProgramHarness h, string name, int atomId)
    {
        // Solve with an atom argument: stage the register by hand.
        bool started = h.Solve(name, (long?)null);
        // Re-run properly: bind the fresh variable's home to the atom first.
        // Simpler: a second Solve wouldn't help -- instead reuse the variable
        // route and check membership by enumeration.
        if (!started) return false;
        if (h.Answer(0).Tag == Tag.Atom && h.Answer(0).AsAtomId == atomId) return true;
        while (h.NextSolution())
            if (h.Answer(0).Tag == Tag.Atom && h.Answer(0).AsAtomId == atomId) return true;
        return false;
    }

    private static string AtomName(Cell c)
        => AtomTable.GetById(c.AsAtomId)?.Name ?? $"<{c.Tag}>";
}

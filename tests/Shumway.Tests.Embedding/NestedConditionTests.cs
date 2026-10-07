using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>An if-then-else nested as the condition of another reaches a
/// call/N boundary per level, and each boundary distributes the caller's
/// module over the construct. Tagging a goal that carries a tag already
/// nested one more '$mqual' per level: the construct grew with every
/// boundary, and so did the heap. Measured in cells the run allocates, a
/// deterministic count: twice the depth must cost about twice the cells.
/// </summary>
public sealed partial class NestedConditionTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public run/1.
        run(G) :- call(G).
        cond(0, G, G) :- !.
        cond(N, G, (C -> true)) :- N1 is N - 1, cond(N1, G, C).
        cells_for(N, Cells) :-
            cond(N, X = 1, C),
            cells_allocated(C0), run(C), cells_allocated(C1),
            X == 1,
            Cells is C1 - C0.
        """;

    public sealed partial class Probe
    {
        [PrologPredicate("cells_allocated/1")]
        public static long CellsAllocated(Activation engine) => engine.CellsAllocated;
    }

    private long Cells(PrologEngine e, int depth)
    {
        var r = e.Query($"cells_for({depth}, Cells).");
        Assert.True(r.Success, $"cells_for({depth}) failed");
        long cells = long.Parse(r["Cells"]!.ToString()!);
        o.WriteLine($"depth {depth}: {cells} cells");
        return cells;
    }

    private void Linear(PrologEngine e)
    {
        long small = Cells(e, 400), large = Cells(e, 800);
        Assert.True(large < 3 * small,
            $"800 levels cost {large} cells against {small} for 400: more than linear");
    }

    [Fact]
    public void Tier0()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.RegisterPredicates<Probe>();
        e.ConsultString(Corpus);
        Linear(e);
    }

    [Fact]
    public void Il()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.RegisterPredicates<Probe>();
        e.ConsultString(Corpus);
        Assert.True(e.Query("run(true).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.IlPromotion.IsPromoted(FunctorTable.Intern(AtomTable.Intern("run").Id, 1)),
            "run/1 did not promote: this would be Tier-0 again");
        Linear(e);
    }
}

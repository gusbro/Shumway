using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Dispatching a predicate that is not compiled allocates nothing:
/// with no tier, or below the threshold, it is what every call of the program
/// goes through.</summary>
public sealed class DispatchAllocationTests
{
    // call/2 on a goal that arrives as an argument: a dispatch at run time.
    private const string Program = """
        step(_).
        loop(0, _) :- !.
        loop(N, G) :- call(G, N), M is N - 1, loop(M, G).
        """;

    private static long Allocated(PrologEngine e, string goal)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(e.Query(goal).Success, goal);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Theory]
    [InlineData(0)]            // no tier
    [InlineData(1_000_000)]    // a threshold these calls stay below
    public void AMetaCallOfAnUncompiledPredicate_AllocatesNothing(int threshold)
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        e.IlPromotion.Threshold = threshold;
        // The engine's own areas reach their size first.
        for (int i = 0; i < 3; i++)
        {
            Allocated(e, "loop(2000, step).");
            Allocated(e, "loop(200, step).");
        }
        long few = Allocated(e, "loop(200, step).");
        long many = Allocated(e, "loop(2000, step).");
        // 1800 more dispatches. A closure per dispatch is 32 bytes or more each.
        Assert.True(many - few < 1800 * 8, $"1800 dispatches allocated {many - few} bytes");
    }
}

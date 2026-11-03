using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Building a term with <c>=..</c> or <c>functor/3</c> allocates
/// nothing per call. The culprit an error would carry is read only when the
/// error is raised: reading it on every call copied the list each time.</summary>
public sealed class TermConstructionAllocationTests
{
    private const string Program = """
        univ_loop(0) :- !.
        univ_loop(N) :-
            T =.. [f, a, b, c, d, e, f, g, h], arg(8, T, h),
            M is N - 1, univ_loop(M).
        functor_loop(0) :- !.
        functor_loop(N) :-
            functor(T, g, 3), arg(1, T, x),
            M is N - 1, functor_loop(M).
        """;

    private static long Allocated(PrologEngine e, string goal)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(e.Query(goal).Success, goal);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Theory]
    [InlineData("univ_loop")]
    [InlineData("functor_loop")]
    public void BuildingATerm_AllocatesNothingPerCall(string loop)
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        e.IlPromotion.Threshold = 0;
        // The engine's own areas reach their size first.
        for (int i = 0; i < 3; i++)
        {
            Allocated(e, $"{loop}(2000).");
            Allocated(e, $"{loop}(200).");
        }
        long few = Allocated(e, $"{loop}(200).");
        long many = Allocated(e, $"{loop}(2000).");
        // 1800 more calls. Reading one culprit is an object of 24 bytes or more.
        Assert.True(many - few < 1800 * 8, $"1800 calls of {loop} allocated {many - few} bytes");
    }
}

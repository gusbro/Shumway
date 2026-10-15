using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A predicate compiled as a member of another's region has its code
/// there, and a call from anywhere runs it: the member takes the region's code,
/// entered where it starts, rather than the interpreter until it is compiled a
/// second time on its own.</summary>
public sealed class RegionMemberEntryTests
{
    private const string Program = @"
:- public loop/1.
:- public q/1.
loop(0) :- !.
loop(N) :- q(N), N1 is N - 1, loop(N1).
q(X) :- nop, X > -1.
nop.
";

    // Synchronous: loop/1 compiles at its first call, before q/1 ever runs on
    // its own, so q/1 is a member of loop/1's region.
    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(Program);
        return e;
    }

    private static int Fid(string name, int arity) => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    // Goals the interpreter dispatched while running Goal.
    private static long Dispatches(PrologEngine e, string goal)
    {
        var r = e.Query($"statistics(inferences, I0), {goal}, statistics(inferences, I1), D is I1 - I0.");
        Assert.True(r.Success, goal);
        return long.Parse(r["D"]!.ToString()!);
    }

    private const string HundredCalls = @"\+ (between(1, 100, X), \+ q(X))";

    [Fact]
    public void AMemberCalledOnItsOwnRunsItsRegionsCode()
    {
        var tier0 = Engine(0);
        var e = Engine(1);
        Assert.True(e.Query("loop(10).").Success);
        Assert.Equal(Fid("loop", 1), e.IlPromotion.RegionRootOf(Fid("q", 1)));
        Assert.True(e.IlPromotion.IsPromoted(Fid("q", 1)));

        // Each q/1 the interpreter calls dispatches nop/0 too; in the region,
        // nop/0 is a branch.
        long interpreted = Dispatches(tier0, HundredCalls);
        long compiled = Dispatches(e, HundredCalls);
        Assert.True(interpreted - compiled >= 90, $"compiled {compiled}, interpreted {interpreted}");
    }

    [Fact]
    public void AMemberWithCodeOfItsOwnKeepsIt()
    {
        var e = Engine(1);
        Assert.True(e.Query("q(1).").Success);
        Assert.True(e.Query("loop(10).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("q", 1)));
        Assert.Null(e.IlPromotion.RegionRootOf(Fid("q", 1)));
    }

    [Fact]
    public void AMemberGoesWithItsRoot()
    {
        var e = Engine(1);
        Assert.True(e.Query("loop(10).").Success);
        Assert.Equal(Fid("loop", 1), e.IlPromotion.RegionRootOf(Fid("q", 1)));

        // A clause for loop/1 from another source: the root's code goes, and
        // with it the member's.
        e.ConsultString(":- public loop/1.\nloop(-1).\n");
        Assert.False(e.IlPromotion.IsPromoted(Fid("loop", 1)));
        Assert.False(e.IlPromotion.IsPromoted(Fid("q", 1)));
        Assert.Null(e.IlPromotion.RegionRootOf(Fid("q", 1)));
        Assert.True(e.Query("q(5), loop(-1), loop(10).").Success);
    }
}

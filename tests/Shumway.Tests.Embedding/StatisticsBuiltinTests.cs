using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <c>statistics/2</c> — the SWI/Scryer/GNU timing idiom. Enough to let a
/// benchmark self-time its own Prolog compute (no process-startup noise).
/// </summary>
public class StatisticsBuiltinTests
{
    [Fact]
    public void Runtime_UnifiesWithTwoElementMsList()
    {
        var e = new PrologEngine();
        var s = e.Query("statistics(runtime, [Total, SinceLast]).");
        Assert.True(s.Success);
        Assert.IsType<IntTerm>(s["Total"]);
        Assert.IsType<IntTerm>(s["SinceLast"]);
        Assert.True(((IntTerm)s["Total"]!).Value >= 0);
    }

    [Fact]
    public void Runtime_SinceLast_TimesTheGoalBetweenTwoCalls()
    {
        var e = new PrologEngine();
        // The classic idiom: reset, do work, read the delta. The delta is
        // non-negative and no larger than the total runtime.
        var s = e.Query(
            "statistics(runtime, _), " +
            "( between(1, 200000, _), fail ; true ), " +
            "statistics(runtime, [Total, SinceLast]).");
        Assert.True(s.Success);
        long sinceLast = ((IntTerm)s["SinceLast"]!).Value;
        long total = ((IntTerm)s["Total"]!).Value;
        Assert.True(sinceLast >= 0);
        Assert.True(sinceLast <= total);
    }

    [Fact]
    public void Walltime_And_Cputime_Work()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("statistics(walltime, [_, _]).").Success);
        var s = e.Query("statistics(cputime, T).");
        Assert.True(s.Success);
        Assert.True(s["T"] is FloatTerm or IntTerm);
    }

    [Fact]
    public void UnknownKey_IsLenient()
    {
        var e = new PrologEngine();
        // A key we do not special-case unifies with [0, 0] rather than failing,
        // so a program probing several keys keeps working.
        Assert.True(e.Query("statistics(some_unknown_key, [0, 0]).").Success);
    }
}

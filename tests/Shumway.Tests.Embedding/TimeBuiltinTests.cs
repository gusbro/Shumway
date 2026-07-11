using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// SWI-style <c>time/1</c> — a prelude meta-predicate that calls Goal like
/// <c>call/1</c> and prints a per-answer resource report (inferences =
/// Tier-0 goal dispatches, elapsed seconds, heap cells allocated, Lips) to
/// the engine's output. Non-determinism is preserved (each further answer
/// reports the DELTA since the previous one) and exhausting the goal prints
/// a final report before failing — matching SWI's REPL prototyping
/// behaviour.
/// </summary>
public class TimeBuiltinTests
{
    private static (PrologEngine Engine, StringWriter Out) Engine(string program = "")
    {
        var e = new PrologEngine();
        var sw = new StringWriter();
        e.Out = sw;
        if (program.Length > 0) e.ConsultString(program);
        return (e, sw);
    }

    [Fact]
    public void Time_Transparent_BindingsAndSuccess()
    {
        var (e, _) = Engine("p(41).\n");
        var sol = e.Query("time(p(X)).");
        Assert.True(sol.Success);
        Assert.Equal("41", sol["X"]!.ToString());
    }

    [Fact]
    public void Time_PrintsReportLine()
    {
        var (e, sw) = Engine("p(1).\n");
        Assert.True(e.Query("time(p(_)).").Success);
        string outp = sw.ToString();
        Assert.Contains("inferences,", outp);
        Assert.Contains("seconds,", outp);
        Assert.Contains("heap cells", outp);
        Assert.Contains("Lips)", outp);
        Assert.StartsWith("%", outp.TrimStart());
    }

    [Fact]
    public void Time_NonDeterminism_Preserved_ReportPerAnswer()
    {
        var (e, sw) = Engine();
        var all = System.Linq.Enumerable.ToList(
            e.QueryAll("time(member(X, [a, b, c]))."));
        Assert.Equal(3, all.Count);
        // One report per answer plus the final exhausted-redo report.
        int reports = sw.ToString().Split("inferences,").Length - 1;
        Assert.Equal(4, reports);
    }

    [Fact]
    public void Time_GoalFails_ReportsThenFails()
    {
        var (e, sw) = Engine();
        Assert.False(e.Query("time(fail).").Success);
        Assert.Contains("inferences,", sw.ToString());
    }

    [Fact]
    public void Time_CutInsideGoal_IsLocal()
    {
        // The cut inside the timed goal must not cut the caller: q/1 still
        // enumerates both branches of the outer disjunction.
        var (e, _) = Engine("r(1).\nr(2).\n");
        var all = System.Linq.Enumerable.ToList(
            e.QueryAll("( time((r(X), !)) ; X = outer )."));
        Assert.Equal(2, all.Count);
        Assert.Equal("1", all[0]["X"]!.ToString());
        Assert.Equal("outer", all[1]["X"]!.ToString());
    }

    [Fact]
    public void Time_CountsGrowWithWork()
    {
        // A bigger workload must report (weakly) more inferences than a tiny
        // one — parse both reports' leading counts.
        var (e, sw) = Engine(
            "count(0) :- !.\ncount(N) :- M is N - 1, count(M).\n");
        Assert.True(e.Query("time(count(5)).").Success);
        Assert.True(e.Query("time(count(5000)).").Success);
        string[] lines = sw.ToString().Split('\n');
        long Parse(string l) => long.Parse(
            l.Split("inferences")[0].Replace("%", "").Replace(",", "").Trim());
        long small = -1, big = -1;
        foreach (var l in lines)
            if (l.Contains("inferences,"))
            {
                if (small < 0) small = Parse(l);
                else big = Parse(l);
            }
        Assert.True(small > 0);
        Assert.True(big > small * 100,
            $"expected big >> small, got small={small} big={big}");
    }
}

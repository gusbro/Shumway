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
    public void Statistics0_ReportsBothGarbageCollectors()
    {
        // The report names the two collectors (ADR-016 heap GC per
        // activation, ADR-003 atom GC process-wide) with their run counts
        // and reclaim totals, alongside the resource lines.
        var e = new PrologEngine();
        var s = e.Query("with_output_to(atom(A), statistics).");
        Assert.True(s.Success);
        string report = ((Shumway.Compiler.Ast.AtomTerm)s["A"]!).Name;
        Assert.Contains("Heap GC:", report);
        Assert.Contains("cells reclaimed", report);
        Assert.Contains("Atoms:", report);
        Assert.Contains("atoms reclaimed", report);
        Assert.Contains("permanent", report);
    }

    [Fact]
    public void HeapGcCounters_AccumulateAcrossCollections()
    {
        // CollectHeap bumps the count and accumulates the reclaim total
        // only when something was actually reclaimed.
        var e = new PrologEngine();
        var act = new Shumway.Core.Activation();
        Assert.Equal(0, act.HeapGcCount);
        act.AllocateHeapUnbound();           // garbage: nothing roots it
        int reclaimed = act.CollectHeap();
        Assert.Equal(reclaimed > 0 ? 1 : 0, act.HeapGcCount);
        Assert.Equal(reclaimed, act.HeapGcReclaimedCells);
    }

    [Fact]
    public void GarbageCollect_ReclaimsDeadStructures_DespiteStaleRegisters()
    {
        // The big-system failure mode this pins: a returned goal leaves a
        // stale X register naming its dead structure, and a conservative
        // register scan would root it forever. garbage_collect/0 runs with
        // a live-register bound of zero (its own arity), so the dead list
        // is reclaimed and the stale registers are cleared.
        var e = new PrologEngine();
        e.ConsultString("""
            mklist(0, []) :- !.
            mklist(N, [M|T]) :- M is N - 1, mklist(M, T).
            gengarbage :- mklist(50000, L), length(L, K), K > 0.
            """);
        var sol = e.Query(
            "gengarbage, garbage_collect, "
            + "with_output_to(atom(A), statistics), atom(A).");
        Assert.True(sol.Success);
        string report = ((Shumway.Compiler.Ast.AtomTerm)sol["A"]!).Name;
        // The reclaim happened and the report says so: at least one
        // collection, and the in-use count is back to a trivial residue
        // (the 100k-cell list is gone).
        Assert.Contains("collection", report);
        var m = System.Text.RegularExpressions.Regex.Match(
            report, @"Heap:\s+([\d,]+) cells");
        Assert.True(m.Success, report);
        long inUse = long.Parse(m.Groups[1].Value.Replace(",", ""));
        Assert.True(inUse < 10_000, $"heap still holds {inUse} cells: {report}");
    }

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

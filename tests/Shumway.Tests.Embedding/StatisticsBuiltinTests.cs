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
        // The reclaim happened and the report says so: at least one COMPACTING
        // collection, and the in-use count is back to a trivial residue (the
        // 100k-cell list is gone). Asserted as the number rather than the word:
        // a collector that ran and moved nothing also used to read as
        // "collection".
        var gc = System.Text.RegularExpressions.Regex.Match(
            report, @"Heap GC:\s+([\d,]+) runs?, ([\d,]+) compacting");
        Assert.True(gc.Success, report);
        Assert.True(int.Parse(gc.Groups[2].Value.Replace(",", "")) >= 1, report);
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

    [Fact]
    public void TheReportSeparatesRunningFromCompacting()
    {
        // A collector that ran and found the whole heap live is not a
        // collector that never ran. Reporting only the compacting ones said
        // `0 collections` for both, which reads as "the GC never engaged" on a
        // query whose memory came back through backtracking instead.
        var e = new PrologEngine();
        e.ConsultString("""
            mklist(0, []) :- !.
            mklist(N, [M|T]) :- M is N - 1, mklist(M, T).
            churn(0) :- !.
            churn(K) :- mklist(200000, L), length(L, _), K1 is K - 1, churn(K1).
            """);
        var sol = e.Query("churn(8), with_output_to(atom(A), statistics).");
        Assert.True(sol.Success);
        string report = ((Shumway.Compiler.Ast.AtomTerm)sol["A"]!).Name;
        var gc = System.Text.RegularExpressions.Regex.Match(
            report, @"Heap GC:\s+([\d,]+) runs?, ([\d,]+) compacting, ([\d,]+) cells");
        Assert.True(gc.Success, report);
        int runs = int.Parse(gc.Groups[1].Value.Replace(",", ""));
        int compacting = int.Parse(gc.Groups[2].Value.Replace(",", ""));
        // Allocation across goal boundaries reaches the safe points, so this
        // shape does collect, and every run of it had something to move.
        Assert.True(runs >= 1, report);
        Assert.True(compacting <= runs, report);
        Assert.True(compacting >= 1, report);
    }

    [Fact]
    public void TheCollectorTotalsSurviveTheQueryThatEarnedThem()
    {
        // A query gets a fresh Activation, so the collector's counters die
        // with it. statistics/0 is typed at a TOP LEVEL, where the question
        // is what the session has done -- per-query counters answered it with
        // the same zeroes however much work had gone by.
        var e = new PrologEngine();
        e.ConsultString("""
            mklist(0, []) :- !.
            mklist(N, [M|T]) :- M is N - 1, mklist(M, T).
            churn(0) :- !.
            churn(K) :- mklist(200000, L), length(L, _), K1 is K - 1, churn(K1).
            """);

        long Runs()
        {
            var sol = e.Query("with_output_to(atom(A), statistics).");
            string report = ((Shumway.Compiler.Ast.AtomTerm)sol["A"]!).Name;
            var m = System.Text.RegularExpressions.Regex.Match(
                report, @"Heap GC:\s+([\d,]+) runs?");
            Assert.True(m.Success, report);
            return long.Parse(m.Groups[1].Value.Replace(",", ""));
        }

        Assert.True(e.Query("churn(6).").Success);
        long after = Runs();
        Assert.True(after >= 1, $"no collection was counted: {after}");
        Assert.True(e.Query("churn(6).").Success);
        Assert.True(Runs() > after, "the totals did not grow with the second run");
    }
}

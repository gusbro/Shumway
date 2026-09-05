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
    public void AnUnknownKeyIsRefused()
    {
        // Answering an unknown key with [0, 0] reads as a measurement and is
        // not one: a program probing several keys was told every one of them
        // cost nothing.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(statistics(some_unknown_key, _), "
            + "error(domain_error(statistics_key, some_unknown_key), _), true).").Success);
        // And it no longer answers the old lie, in the loudest way available:
        // an uncaught ball rather than a quiet [0, 0].
        Assert.Throws<Shumway.Embedding.ShumwayPrologException>(
            () => e.Query("statistics(some_unknown_key, [0, 0])."));
    }

    [Fact]
    public void ANonAtomKeyNamesItselfInTheError()
    {
        // The culprit slot has to carry the key that was passed. An unbound
        // variable there says nothing about what went wrong.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(statistics(1+1, _), error(domain_error(statistics_key, 1+1), _), true).")
            .Success);
        Assert.True(e.Query(
            "catch(statistics(_, _), error(instantiation_error, _), true).").Success);
    }

    [Theory]
    // The keys of the dialect this predicate comes from, answered from the
    // running query's own areas rather than with zeroes.
    [InlineData("user_time")]
    [InlineData("system_time")]
    [InlineData("cpu_time")]
    public void TheCpuTimeKeysGiveAPair(string key)
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"statistics({key}, [Total, Since]), integer(Total), integer(Since).").Success);
    }

    [Fact]
    public void TheAreaKeysReportTheRunningQuery()
    {
        var e = new PrologEngine();
        // The heap of a query that has just built a term is not empty, and
        // that is the difference between a real number and a placeholder.
        var s = e.Query("length(L, 100), statistics(global_stack, [Used, Free]).");
        Assert.True(s.Success);
        Assert.True(((IntTerm)s["Used"]!).Value > 0);
        Assert.True(((IntTerm)s["Free"]!).Value >= 0);
        Assert.True(e.Query("statistics(local_stack, [_, _]).").Success);
        Assert.True(e.Query("statistics(trail_stack, [_, _]).").Success);
        Assert.True(e.Query("statistics(cstr_stack, [_, _]).").Success);
        Assert.True(e.Query("statistics(atoms, [N, _]), N > 100.").Success);
    }

    [Fact]
    public void EachKeyCountsItsOwnDelta()
    {
        // Two keys share no reference: asking runtime must not zero the
        // walltime delta, or the classic timing idiom reports nothing when a
        // program measures both.
        var e = new PrologEngine();
        Assert.True(e.Query("statistics(runtime, _), statistics(walltime, _).").Success);
        var s = e.Query(
            "( between(1, 200000, _), fail ; true ), statistics(walltime, [_, D]).");
        Assert.True(s.Success);
        Assert.True(((IntTerm)s["D"]!).Value >= 0);
    }

    [Fact]
    public void TheReportSeparatesRunningFromCompacting()
    {
        // A collector that ran and found the whole heap live is not a
        // collector that never ran. Reporting only the compacting ones said
        // `0 collections` for both, which reads as "the GC never engaged" on a
        // query whose memory came back through backtracking instead.
        //
        // garbage_collect/0 rather than megabytes of garbage: the claim here is
        // about the REPORT, and forcing a run states it without waiting for the
        // watermark. (That the compacting count tracks a real reclaim is what
        // GarbageCollect_ReclaimsDeadStructures_DespiteStaleRegisters pins.)
        var e = new PrologEngine();
        // A little garbage first: an empty heap makes CollectHeap return
        // before it counts anything, there being nothing there at all.
        var sol = e.Query(
            "numlist(1, 1000, _), garbage_collect, with_output_to(atom(A), statistics).");
        Assert.True(sol.Success);
        string report = ((Shumway.Compiler.Ast.AtomTerm)sol["A"]!).Name;
        var gc = System.Text.RegularExpressions.Regex.Match(
            report, @"Heap GC:\s+([\d,]+) runs?, ([\d,]+) compacting, ([\d,]+) cells");
        Assert.True(gc.Success, report);
        int runs = int.Parse(gc.Groups[1].Value.Replace(",", ""));
        int compacting = int.Parse(gc.Groups[2].Value.Replace(",", ""));
        Assert.True(runs >= 1, report);
        Assert.True(compacting <= runs, report);
    }

    [Fact]
    public void TheCollectorTotalsSurviveTheQueryThatEarnedThem()
    {
        // A query gets a fresh Activation, so the collector's counters die with
        // it. statistics/0 is typed at a TOP LEVEL, where the question is what
        // the SESSION has done -- per-query counters answered it with the same
        // zeroes however much work had gone by. Each garbage_collect/0 below is
        // its own query, so the growth is exactly what used to be lost.
        var e = new PrologEngine();

        long Runs()
        {
            var sol = e.Query("with_output_to(atom(A), statistics).");
            string report = ((Shumway.Compiler.Ast.AtomTerm)sol["A"]!).Name;
            var m = System.Text.RegularExpressions.Regex.Match(
                report, @"Heap GC:\s+([\d,]+) runs?");
            Assert.True(m.Success, report);
            return long.Parse(m.Groups[1].Value.Replace(",", ""));
        }

        Assert.True(e.Query("numlist(1, 1000, _), garbage_collect.").Success);
        long after = Runs();
        Assert.True(after >= 1, $"no collector run was counted: {after}");
        Assert.True(e.Query("numlist(1, 1000, _), garbage_collect.").Success);
        Assert.True(Runs() > after, "the totals did not grow with the second query");
    }

    [Fact]
    public void TheResidualCopyIsSkippedWhenNothingCarriesAttributes()
    {
        // The top level wraps every query in copy_term/3 to project residual
        // constraints, which copies the WHOLE answer for every solution.
        // '$any_attvars' is how it learns in O(1) that there is nothing to
        // project -- and it has to be right in both directions.
        var e = new PrologEngine();
        Assert.False(e.Query("'$any_attvars'.").Success);
        e.UseCoroutining();
        Assert.True(e.Query("freeze(X, true), '$any_attvars'.").Success);
        // And the attribute goes away with the query that made it.
        Assert.False(e.Query("'$any_attvars'.").Success);
    }
}

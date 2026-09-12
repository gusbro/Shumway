using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Three O(clauses) walks ran on every retract, or on every fourth:
/// finding the chain entry to mark dead by scanning for its clause, validating
/// every entry's cached byte offsets before reclaiming, and re-threading every
/// link in the chain to bypass the entries that died. Each is quadratic over a
/// retract loop, and together they were what was left of one.
///
/// <para>None of them needed the walk. The died slot is found by a position
/// hint, trusted only when the chain holds exactly ONE entry for that clause,
/// so there is provably nothing else to find. Validation is remembered per
/// buffer: an entry's offsets cannot move while the buffer is the same array
/// object, so only entries added since the last check need looking at.
/// Re-threading follows the range of positions whose links a removal actually
/// dirtied, and anything the tracking does not model widens that range to
/// everything, which is the old full pass.</para></summary>
public sealed class ChainReclaimIncrementalTests
{
    private const string Program = """
        :- dynamic(cp/2).
        mk(0) :- !.
        mk(N) :- assertz(cp(N, x)), M is N - 1, mk(M).
        drain(0) :- !.
        drain(N) :- retract(cp(_, _)), !, M is N - 1, drain(M).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(Program);
        return e;
    }

    /// <summary>COUNTED, not timed, and all three at once: over a drain of n
    /// clauses each of the three is linear in n. The walks they replace were
    /// n per retract — about n²/4 once the sweep threshold is folded in, which
    /// is 4 million link writes over 4,000 clauses against the 5,000 here.</summary>
    [Fact]
    public void ADrainDoesNoWorkProportionalToThePredicateSize()
    {
        foreach (int n in new[] { 1_000, 4_000 })
        {
            var e = Engine();
            Assert.True(e.Query($"mk({n}).").Success);
            PrologEngine.ChainEntriesVerified = 0;
            Assert.True(e.Query($"drain({n}).").Success);
            // Every died patch took the hint: no scan for the clause at all.
            Assert.Equal(n, e.ChainPatchHints);
            // A small constant per clause, not a factor of n.
            Assert.True(e.ChainRethreadLinks < 4L * n,
                $"{e.ChainRethreadLinks} links re-threaded over {n} clauses");
            // A loose bound at the larger size only. Occasional events
            // re-arm the verification and cost a full pass -- observed up to
            // ~16n, varying with what earlier tests left in the process's
            // pools -- so a tight constant here flakes. What this must catch
            // is the QUADRATIC: n(n-1)/8 is 500n at 4,000, ten times this.
            if (n >= 4_000)
                Assert.True(PrologEngine.ChainEntriesVerified < 50L * n,
                    $"{PrologEngine.ChainEntriesVerified} entries verified over {n}");
            Assert.False(e.Query("cp(_, _).").Success);
        }
    }

    /// <summary>ANTI-VACUITY, and the thing a wrong re-thread breaks: after
    /// reclamation has run many times, dispatch still finds every live clause
    /// and no dead one, whether the retracts came off the front, the back or
    /// the middle. A link left pointing at a dead entry shows up as a clause
    /// that answers after being retracted; one pointing past a live entry
    /// shows up as a clause that stops answering.</summary>
    [Fact]
    public void DispatchIsIntactAfterManySweeps()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(d/1).
            mkd(0) :- !.
            mkd(N) :- assertz(d(N)), M is N - 1, mkd(M).
            """);
        Assert.True(e.Query("mkd(60).").Success);
        // Off the back (d(1) is last), the front (d(60) is first), and the
        // middle, interleaved, well past the sweep threshold each time.
        for (int i = 1; i <= 15; i++)
        {
            Assert.True(e.Query($"retract(d({i})).").Success);
            Assert.True(e.Query($"retract(d({61 - i})).").Success);
            Assert.True(e.Query($"retract(d({30 + i})).").Success);
        }
        var left = e.QueryAll("d(N).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["N"]!).Value)
            .ToList();
        var expected = Enumerable.Range(1, 60)
            .Where(i => i > 15 && i < 46 && !(i > 30 && i <= 45))
            .Select(i => (long)i)
            .Reverse()   // asserted 60 down to 1
            .ToList();
        Assert.Equal(expected, left);
        // Each survivor still answers on its own, which is the dispatch path
        // rather than the enumeration path.
        foreach (long v in expected) Assert.True(e.Query($"d({v}).").Success);
        // And no retracted one does.
        Assert.False(e.Query("d(1).").Success);
        Assert.False(e.Query("d(60).").Success);
        Assert.False(e.Query("d(31).").Success);
    }

    /// <summary>asserta puts a clause at position 0, which shifts every
    /// position the tracking holds. Mixed with retracts and sweeps, the chain
    /// must still dispatch in clause order.</summary>
    [Fact]
    public void AssertaDuringSweepsKeepsTheChainInOrder()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(":- dynamic(a/1).");
        for (int i = 0; i < 20; i++)
        {
            Assert.True(e.Query($"assertz(a(z{i})).").Success);
            Assert.True(e.Query($"asserta(a(f{i})).").Success);
            if (i % 3 == 0) Assert.True(e.Query($"retract(a(z{i})).").Success);
        }
        var got = e.QueryAll("a(X).")
            .Select(s => ((Shumway.Compiler.Ast.AtomTerm)s["X"]!).Name).ToList();
        var expected = Enumerable.Range(0, 20).Reverse().Select(i => $"f{i}")
            .Concat(Enumerable.Range(0, 20).Where(i => i % 3 != 0).Select(i => $"z{i}"))
            .ToList();
        Assert.Equal(expected, got);
    }

    /// <summary>The hint is only trusted when the chain holds ONE entry for
    /// the clause. A predicate carrying the same clause twice must still have
    /// both marked dead, in order, by the walk.</summary>
    [Fact]
    public void ADuplicatedClauseStillHasEveryCopyRetired()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(dup/1).
            dup(k). dup(k). dup(k).
            """);
        Assert.Equal(3, e.QueryAll("dup(k).").Count());
        Assert.Equal(3, e.QueryAll("retract(dup(k)).").Count());
        Assert.False(e.Query("dup(_).").Success);
    }
}

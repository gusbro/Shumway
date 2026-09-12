using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Retracted clauses stay linked in the dispatch chain until no
/// choice point can still be walking it — that is what makes the logical
/// update view work — and reclamation sweeps them out once four have piled
/// up. Deciding whether a choice point is inside the chain meant building a
/// set of every chunk address the chain owns, on every sweep: O(clauses) of
/// allocation and hashing per four clauses reclaimed, which is quadratic over
/// a drain and was its single biggest cost.
///
/// <para>The chain now carries an address envelope, maintained as entries come
/// and go, so a saved BP outside it is rejected with one compare. The envelope
/// is conservative — another predicate's chunks can lie between ours — so a BP
/// that falls inside still builds the exact set and the set still decides.
/// Nothing about WHEN reclamation is allowed changed.</para></summary>
public sealed class DeadChainReclaimScanTests
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

    /// <summary>COUNTED, not timed. A drain reclaims constantly and never
    /// needs the exact set, at either size — the envelope answers every
    /// choice point. Before, the set was built once per sweep.</summary>
    [Fact]
    public void ADrainNeverBuildsTheChainAddressSet()
    {
        foreach (int n in new[] { 2_000, 8_000 })
        {
            var e = Engine();
            Assert.True(e.Query($"mk({n}).").Success);
            Assert.True(e.Query($"drain({n}).").Success);
            Assert.Equal(0L, e.ChainAddressSetsBuilt);
            // ANTI-VACUITY: reclamation really did run, many times, and the
            // predicate really is empty.
            Assert.True(e.ChainReclaims > 10, $"only {e.ChainReclaims} sweeps");
            Assert.False(e.Query("cp(_, _).").Success);
        }
    }

    /// <summary>The envelope only ever rejects. A choice point parked inside
    /// the chain — an open enumeration of the very predicate being retracted
    /// from — must still be found, which means the exact set gets built and
    /// the sweep is refused. The clauses the enumeration has not reached yet
    /// are still there for it.</summary>
    [Fact]
    public void AnEnumerationInsideTheChainIsStillFound()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(d/1).
            mkd(0) :- !.
            mkd(N) :- assertz(d(N)), M is N - 1, mkd(M).
            """);
        Assert.True(e.Query("mkd(40).").Success);
        // Walk d/1 with a live choice point while retracting enough clauses
        // to cross the reclaim threshold several times over.
        var got = e.QueryAll("d(X), retract(d(X)).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["X"]!).Value)
            .ToList();
        Assert.Equal(40, got.Count);
        Assert.False(e.Query("d(_).").Success);
        // The enumeration's own choice point sits in the chain, so the
        // envelope could not reject it and the exact set decided.
        Assert.True(e.ChainAddressSetsBuilt > 0,
            "the set was never built, so nothing exercised the exact path");
    }

    /// <summary>Reclamation must not change what the predicate answers,
    /// whatever the envelope says: asserts and retracts interleaved across
    /// the threshold leave exactly the clauses that should be left.</summary>
    [Fact]
    public void InterleavedAssertsAndRetractsLeaveTheRightClauses()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(k/1).
            churn(0) :- !.
            churn(N) :- assertz(k(N)), ( 0 is N mod 2 -> retract(k(N)) ; true ),
                        M is N - 1, churn(M).
            """);
        Assert.True(e.Query("churn(60).").Success);
        var left = e.QueryAll("k(N).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["N"]!).Value)
            .ToList();
        // Every odd 1..59, in assert order (60 counts down to 1).
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (long)(59 - 2 * i)), left);
    }
}

using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Reclaiming a dynamic chain's dead clauses is refused outright when
/// the chain holds a clause emitted inside a source block, because such a
/// chunk is not individually relocatable. That question was answered by
/// walking every entry in the chain -- on every sweep, which is every fourth
/// retract.
///
/// <para>It is invisible until the predicate is large: under 32,000 clauses
/// the other costs hide it, and at 128,000 it was 18 of the 28 seconds of a
/// drain. It is a count, maintained as entries come and go.</para></summary>
public sealed class SourceBlockChainScanTests
{
    private const string Program = """
        :- dynamic(tok/1).
        mk(0) :- !.
        mk(N) :- assertz(tok(N)), M is N - 1, mk(M).
        drain(0) :- !.
        drain(N) :- retract(tok(N)), M is N - 1, drain(M).
        """;

    /// <summary>COUNTED, not timed: reclamation sweeps a drain many times, and
    /// the entries it re-threads and re-verifies stay linear in the predicate
    /// -- the scan this replaces was linear per SWEEP.</summary>
    [Fact]
    public void ReclamationDoesNoWorkProportionalToTheChainPerSweep()
    {
        foreach (int n in new[] { 2_000, 8_000 })
        {
            var e = new PrologEngine { Out = new StringWriter() };
            e.ConsultString(Program);
            Assert.True(e.Query($"mk({n}).").Success);
            PrologEngine.ChainEntriesVerified = 0;
            Assert.True(e.Query($"drain({n}).").Success);
            Assert.True(e.ChainReclaims > 10, $"only {e.ChainReclaims} sweeps");
            Assert.True(e.ChainRethreadLinks < 4L * n,
                $"{e.ChainRethreadLinks} links over {n} clauses");
            Assert.True(PrologEngine.ChainEntriesVerified < 4L * n,
                $"{PrologEngine.ChainEntriesVerified} verified over {n}");
            Assert.False(e.Query("tok(_).").Success);
        }
    }

    /// <summary>ANTI-VACUITY, and the reason the check exists at all: a
    /// predicate whose clauses came from a CONSULTED source block must still
    /// refuse reclamation, and must answer correctly while it does. Retracting
    /// from it leaves exactly the right clauses.</summary>
    [Fact]
    public void AConsultedPredicateStillRetractsCorrectly()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        // Declared dynamic WITH source clauses: these enter through the
        // consult path, not through assertz.
        e.ConsultString("""
            :- dynamic(sb/1).
            sb(1). sb(2). sb(3). sb(4). sb(5). sb(6). sb(7). sb(8).
            """);
        // Well past the reclaim threshold, so a sweep is attempted each time.
        for (int i = 1; i <= 6; i++)
            Assert.True(e.Query($"retract(sb({i})).").Success);
        Assert.Equal(new long[] { 7, 8 },
            e.QueryAll("sb(N).")
                .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["N"]!).Value));
        // And the survivors still dispatch one at a time.
        Assert.True(e.Query("sb(7).").Success);
        Assert.True(e.Query("sb(8).").Success);
        Assert.False(e.Query("sb(3).").Success);
    }

    /// <summary>Source-block clauses and asserted ones mixed in one predicate:
    /// the count has to track both, and retracting the asserted half must not
    /// make the chain look relocatable while a source-block clause is still in
    /// it.</summary>
    [Fact]
    public void MixedSourceAndAssertedClausesRetractCorrectly()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(mx/1).
            mx(s1). mx(s2).
            """);
        for (int i = 0; i < 10; i++)
            Assert.True(e.Query($"assertz(mx(a{i})).").Success);
        for (int i = 0; i < 10; i++)
            Assert.True(e.Query($"retract(mx(a{i})).").Success);
        Assert.Equal(new[] { "s1", "s2" },
            e.QueryAll("mx(X).")
                .Select(s => ((Shumway.Compiler.Ast.AtomTerm)s["X"]!).Name));
        Assert.True(e.Query("retract(mx(s1)).").Success);
        Assert.Single(e.QueryAll("mx(_)."));
        // The count IS the check now, so it has to still describe the chain
        // after all that churn: what it says, and what a walk finds. Read from
        // INSIDE a query, which is the only place a live activation exists.
        int counted = -1, actual = -2;
        Shumway.Builtins.BuiltinsRegistry.Register("$sb_check", 0, act =>
        {
            (counted, actual) = ((PrologEngine)act.Host!).SourceBlockEntryCheck(
                act, Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern("mx").Id, 1));
            return true;
        });
        Assert.True(e.Query("'$sb_check'.").Success);
        Assert.Equal(actual, counted);
    }
}

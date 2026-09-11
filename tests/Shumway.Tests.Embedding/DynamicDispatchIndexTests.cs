using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-041 selects a dynamic predicate's clauses at dispatch by the
/// call's first argument, which is what keeps determinism from depending on
/// whether the predicate has been recompiled indexed yet. It answered the
/// question by WALKING the chain, and re-derived each clause's key as it went
/// -- interning the head atom, per clause, per call.
///
/// <para>So a call cost O(clauses). That is invisible for a predicate built
/// before the query that uses it, because the JIT recompile at query setup
/// gives it a real index. It is not invisible for one built and used INSIDE
/// one query, which never reaches that recompile: 20,000 calls on the same key
/// took 0.86s over 2,000 clauses and 20s over 32,000.</para>
///
/// <para>The key is now decided once per clause and the chain keeps buckets,
/// so the question is a lookup. Order is untouched by construction: the
/// buckets only ever answer "exactly one candidate" or "none", and anything
/// else still runs the chain from its head.</para></summary>
public sealed class DynamicDispatchIndexTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(cp/2).
            mk(0) :- !.
            mk(N) :- assertz(cp(N, x)), M is N - 1, mk(M).
            hit(0, _) :- !.
            hit(N, K) :- cp(K, _), M is N - 1, hit(M, K).
            """);
        return e;
    }

    /// <summary>The shape: a FIXED number of calls, and only the size of the
    /// predicate varies.
    ///
    /// <para>COUNTED, not timed. The cost this is about is C# work inside the
    /// selector, so no Prolog-level counter sees it -- inferences and heap
    /// cells come out identical (40,002 and 20,000) whether the selector
    /// walks 2,000 entries per call or none. And the clock cannot say it
    /// either: the wall time of these runs is not even monotonic in the
    /// clause count on an idle machine, so a ratio bound over it fails on
    /// whichever lane the noise lands badly (it did, on net48-x86).</para>
    ///
    /// <para>What the fix actually claims is that the selector ANSWERS from
    /// its buckets instead of handing the call back to the chain. That is
    /// exact, and it is the same integer on every runtime and every clause
    /// count.</para></summary>
    [Fact]
    public void ACallCostsTheSameWhateverTheClauseCountIs()
    {
        foreach (int clauses in new[] { 2_000, 32_000 })
        {
            var (sole, none, declined) = Verdicts(clauses);
            // Not one of the 20,000 calls was handed back to the chain, at
            // either size. That IS the O(1) claim: a walk cannot produce it
            // (breaking the buckets gives 0 resolved and 20,000 declined).
            Assert.Equal(0L, declined);
            Assert.Equal(0L, none);
            // A floor, not an equality: the tally is per process, so whatever
            // dynamic predicates a previously loaded library leaves in the
            // prelude dispatch here too and add to it.
            Assert.True(sole >= 20_000, $"{clauses} clauses: sole={sole}");
        }
    }

    /// <summary>ANTI-VACUITY: the counters are not simply always these
    /// numbers. A call the buckets cannot answer must DECLINE -- an unbound
    /// first argument leaves every clause a candidate -- and a key no clause
    /// carries must be ruled out without walking.</summary>
    [Fact]
    public void ACallTheBucketsCannotAnswerIsHandedBackToTheChain()
    {
        // An unbound first argument leaves every clause a candidate, so the
        // call is handed back. Here that happens exactly once: the first call
        // BINDS the key, and the 99 after it are answered from the buckets --
        // which is the decline path and its exit in one number.
        var (sole, none, declined) = Verdicts(2_000, "hit(100, _)");
        Assert.Equal(1L, declined);
        Assert.Equal(0L, none);
        Assert.True(sole >= 99, $"sole={sole}");
        // A key no clause has: ruled out, and the call fails -- without the
        // chain ever running.
        var e = Engine();
        Assert.True(e.Query("mk(2000).").Success);
        DynamicCodePatcher.ResetSelCounters();
        DynamicCodePatcher.DynSelDiag = true;
        try { Assert.False(e.Query("cp(999999, _).").Success); }
        finally { DynamicCodePatcher.DynSelDiag = false; }
        Assert.Equal(1L, DynamicCodePatcher.SelNone);
        Assert.Equal(0L, DynamicCodePatcher.SelDeclined);
    }

    /// <summary>ANTI-VACUITY for the answers themselves: selecting one clause
    /// out of thousands must return the RIGHT one, and must leave no choice
    /// point behind it.</summary>
    [Fact]
    public void SelectingOneClauseOutOfThousandsStillAnswersCorrectly()
    {
        var e = Engine();
        Assert.True(e.Query("mk(2000).").Success);
        Assert.Single(e.QueryAll("cp(1500, X)."));
        Assert.True(e.Query("cp(1500, x).").Success);
        Assert.False(e.Query("cp(1500, y).").Success);
        Assert.False(e.Query("cp(2001, _).").Success);
    }

    /// <summary>Runs a fixed number of calls over a predicate of the given
    /// size and reports what the selector decided: (sole, none, declined).
    /// The build and the calls share ONE query, so the JIT's query-setup
    /// recompile cannot index the predicate first -- which is the case this
    /// is about.</summary>
    private static (long Sole, long None, long Declined) Verdicts(
        int clauses, string goal = "hit(20000, 1)")
    {
        var e = Engine();
        Assert.True(e.Query($"mk({clauses}).").Success);
        DynamicCodePatcher.ResetSelCounters();
        DynamicCodePatcher.DynSelDiag = true;
        try { Assert.True(e.Query($"mk(0), {goal}.").Success); }
        finally { DynamicCodePatcher.DynSelDiag = false; }
        return (DynamicCodePatcher.SelSole, DynamicCodePatcher.SelNone,
            DynamicCodePatcher.SelDeclined);

    }

    /// <summary>ANTI-VACUITY, and the whole contract: which clauses run, and
    /// in what order. A keyed clause and a catch-all both match, in clause
    /// order; a key nothing carries matches only the catch-all; a key that
    /// matches nothing at all fails.</summary>
    [Fact]
    public void SelectionPicksTheSameClausesInTheSameOrder()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(u/2).
            u(k1, 1).
            u(k2, 2).
            u(X, var(X)).
            :- dynamic(s/1).
            s(f(1)).
            s(f(2)).
            s(g(1)).
            s([a]).
            s([]).
            """);
        Assert.Equal("[1,var(k1)]", All(e, "u(k1, Y)", "Y"));
        Assert.Equal("[var(nope)]", All(e, "u(nope, Z)", "Z"));
        Assert.Equal("[1,2]", All(e, "s(f(V))", "V"));
        Assert.Equal("[a]", All(e, "s([Q])", "Q"));
        // Unbound first argument: no selection at all, so every clause runs
        // and in order. The third key is a fresh variable, whose printed name
        // is not something to pin.
        var keys = e.QueryAll("u(K, _).").Select(s => s["K"]!.ToString()).ToList();
        Assert.Equal(3, keys.Count);
        Assert.Equal("k1", keys[0]);
        Assert.Equal("k2", keys[1]);
    }

    /// <summary>A first argument no clause can match fails without running
    /// anything, and one that matches exactly one clause leaves no choice
    /// point -- the determinism ADR-041 is there for.</summary>
    [Fact]
    public void NoCandidateFailsAndASoleCandidateIsDeterministic()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(t/1).
            t(a).
            t(b).
            t(c).
            """);
        Assert.False(e.Query("t(zz).").Success);
        Assert.True(e.Query("call_det(t(b), true).").Success);
        Assert.True(e.Query("call_det(t(a), true).").Success);
    }

    private static string All(PrologEngine e, string goal, string v)
        => "[" + string.Join(",",
            e.QueryAll($"{goal}.").Select(s => s[v]!.ToString())) + "]";
}

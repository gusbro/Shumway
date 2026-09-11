using System.Diagnostics;
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
    /// predicate varies. Walking the chain makes the second case sixteen times
    /// the first; a lookup makes it the same.</summary>
    [Fact]
    public void ACallCostsTheSameWhateverTheClauseCountIs()
    {
        double small = Timed(2_000);
        double large = Timed(32_000);
        Assert.True(large < System.Math.Max(small, 0.05) * 6,
            $"20,000 calls took {small:F2}s over 2,000 clauses and "
            + $"{large:F2}s over 32,000");
    }

    private static double Timed(int clauses)
    {
        var e = Engine();
        Assert.True(e.Query($"mk({clauses}).").Success);
        var sw = Stopwatch.StartNew();
        // Same query as the build, so the JIT's query-setup recompile cannot
        // rescue it -- which is the case this is about.
        Assert.True(e.Query($"mk(0), hit(20000, 1).").Success);
        return sw.Elapsed.TotalSeconds;
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

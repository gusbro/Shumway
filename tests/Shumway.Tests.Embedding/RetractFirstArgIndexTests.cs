using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>retract/1 found its clause by trying every clause in the
/// predicate, in order, until one unified. For the classic "retract each key
/// in turn" loop that is O(clauses) per call and quadratic over the loop --
/// 74% of the time of one such loop over 16,000 clauses.
///
/// <para>The store now keeps a first-argument index over the PHYSICAL clause
/// list. It answers a NECESSARY condition and never a verdict: the key comes
/// from a clause's head first argument, which never changes once asserted, and
/// any shape that cannot PROVE a mismatch keys "anything" and stays a
/// candidate for every call. The trial unification still decides.</para>
///
/// <para>That is what makes it sound under the logical update view. Born and
/// died move over time; the key does not. A view can only ever make FEWER
/// clauses visible than the physical list holds, so a clause the index rules
/// out is ruled out for every view, and the index is maintained on physical
/// insertion and removal only -- never on a clause merely becoming
/// invisible.</para></summary>
public sealed class RetractFirstArgIndexTests
{
    private const string Program = """
        :- dynamic(cp/2).
        mk(0) :- !.
        mk(N) :- assertz(cp(N, x)), M is N - 1, mk(M).
        drain(N, N) :- !, retract(cp(N, _)).
        drain(I, N) :- retract(cp(I, _)), J is I + 1, drain(J, N).
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(Program);
        return e;
    }

    /// <summary>COUNTED, not timed: the clauses TRIED is the cost. Retracting
    /// n clauses by key tries n of them -- one per call, the one that matches
    /// -- at both sizes. The scan tried n(n+1)/2 (32 million over 8,000),
    /// which is what made the loop quadratic.
    ///
    /// <para>The keys here are retracted in the opposite order to the one they
    /// were asserted in, so every match sits at the END of the list: the worst
    /// case for the scan, and indifferent to the index.</para></summary>
    [Fact]
    public void RetractingByKeyTriesOneClausePerCall()
    {
        foreach (int n in new[] { 2_000, 8_000 })
        {
            var e = Engine();
            Assert.True(e.Query($"mk({n}).").Success);
            e.RetractCandidatesTried = 0;
            Assert.True(e.Query($"drain(1, {n}).").Success);
            Assert.Equal(n, e.RetractCandidatesTried);
            // ANTI-VACUITY: all of them really went.
            Assert.False(e.Query("cp(_, _).").Success);
            // The index was maintained, never rebuilt from scratch.
            Assert.Equal(0L, e.ClauseIndexRebuilds);
        }
    }

    /// <summary>A clause whose first argument rules nothing out is a candidate
    /// for EVERY call, so it must be tried alongside the keyed ones and in
    /// clause order. Leaving it out would be a solution that never runs.</summary>
    [Fact]
    public void AVariableFirstArgumentIsACandidateForEveryKey()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(w/2).
            w(a, 1).
            w(X, gen(X)).
            w(b, 2).
            """);
        // The general clause precedes w(b, 2) and must win for key b.
        var first = e.QueryAll("retract(w(b, V)).")
            .Select(s => s["V"]!.ToString()).ToList();
        Assert.Equal(2, first.Count);
        Assert.Equal("gen(b)", first[0]);
        Assert.Equal("2", first[1]);
        // Both went, so only the untouched keyed clause is left.
        Assert.Single(e.QueryAll("w(_, _)."));
        // And on a fresh copy, a key NO clause names still reaches the
        // general one -- the index cannot rule it out for anything.
        var e2 = new PrologEngine { Out = new StringWriter() };
        e2.ConsultString("""
            :- dynamic(w/2).
            w(a, 1).
            w(X, gen(X)).
            w(b, 2).
            """);
        Assert.True(e2.Query("retract(w(zzz, gen(zzz))).").Success);
    }

    /// <summary>The unkeyable shapes: a pattern whose first argument is
    /// unbound, a float, or a big integer proves nothing about any clause, so
    /// every clause stays a candidate and the enumeration is complete.</summary>
    [Fact]
    public void AnUnkeyablePatternStillSeesEveryClause()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(m/1).
            m(1). m(a). m(1.5). m(99999999999999999999999). m([x]). m(f(y)).
            """);
        // Unbound: every clause, in order.
        Assert.Equal(6, e.QueryAll("m(_).").Count());
        // A float pattern keys nothing and must still find its clause.
        Assert.True(e.Query("retract(m(1.5)).").Success);
        // So must a big integer.
        Assert.True(e.Query("retract(m(99999999999999999999999)).").Success);
        Assert.Equal(4, e.QueryAll("m(_).").Count());
        // Retracting with an unbound first argument enumerates what is left.
        Assert.Equal(4, e.QueryAll("retract(m(_)).").Count());
    }

    /// <summary>Every key shape the index distinguishes, retracted by key:
    /// atom, integer, list, and compound (by functor AND arity, so f/1 and
    /// f/2 are different keys).</summary>
    [Fact]
    public void EveryKeyShapeSelectsItsOwnClause()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(s/2).
            s(atom, 1).
            s(42, 2).
            s([h|_], 3).
            s(f(_), 4).
            s(f(_, _), 5).
            s("txt", 6).
            """);
        Assert.True(e.Query("retract(s(atom, 1)).").Success);
        Assert.True(e.Query("retract(s(42, 2)).").Success);
        Assert.True(e.Query("retract(s([h, i], 3)).").Success);
        Assert.True(e.Query("retract(s(f(z), 4)).").Success);
        Assert.True(e.Query("retract(s(f(y, z), 5)).").Success);
        // A key that names nothing fails without disturbing the rest.
        Assert.False(e.Query("retract(s(nope, _)).").Success);
        Assert.Single(e.QueryAll("s(_, _)."));
    }

    /// <summary>asserta and assertz both have to land in the index at the
    /// right END of it, or clause order breaks: the index is what decides
    /// which clause a keyed retract reaches first.</summary>
    [Fact]
    public void AssertaAndAssertzKeepClauseOrderInTheIndex()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(":- dynamic(o/2).");
        Assert.True(e.Query("assertz(o(k, 2)), asserta(o(k, 1)), assertz(o(k, 3)).")
            .Success);
        var got = e.QueryAll("retract(o(k, V)).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["V"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 2, 3 }, got);
    }

    /// <summary>A clause asserted after the index was built is in it, and one
    /// retracted is out of it — including the case where the same key is
    /// asserted and retracted repeatedly, which is what a churn idiom does.</summary>
    [Fact]
    public void ChurnOnOneKeyStaysConsistent()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(c/2).
            churn(0) :- !.
            churn(N) :- assertz(c(k, N)), retract(c(k, N)),
                        assertz(c(other, N)), M is N - 1, churn(M).
            """);
        Assert.True(e.Query("churn(200).").Success);
        Assert.False(e.Query("c(k, _).").Success);
        Assert.Equal(200, e.QueryAll("c(other, _).").Count());
        Assert.Equal(0L, e.ClauseIndexRebuilds);
    }
}

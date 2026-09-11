using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A retract/1 that leaves a choice point owes the rest of its
/// candidates, and it used to pay that debt by COPYING them out of the live
/// clause list at call time. The copy is the enumeration's logical update
/// view, so it could not simply be dropped -- but almost nothing ever reads
/// it: the deterministic idiom (<c>retract(X), !</c>, or any retract whose
/// choice point is cut or abandoned) discards it untouched. Over a 16,000
/// clause drain that was 128 million clause slots copied to be thrown away.
///
/// <para>The candidates are now a WINDOW into the live list, and the store
/// reports mutations so the window can stay a view: a change below it shifts
/// it, a change inside it copies it out first (the view must keep a clause
/// someone else retracts), a change above it is nothing. An enumeration's own
/// removals are always below its own window, so it never copies for
/// itself.</para></summary>
public sealed class RetractClauseWindowTests
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

    /// <summary>COUNTED, not timed: the copy is the cost, so the number of
    /// clauses copied is the measure. Zero, and zero at both sizes -- the
    /// eager copy was quadratic in the predicate's size (n(n-1)/2, which is
    /// 128 million slots at 16,000).</summary>
    [Fact]
    public void ADeterministicRetractCopiesNothing()
    {
        foreach (int n in new[] { 2_000, 8_000 })
        {
            var e = Engine();
            Assert.True(e.Query($"mk({n}).").Success);
            e.ClausesCopiedOut = 0;
            Assert.True(e.Query($"drain({n}).").Success);
            Assert.Equal(0L, e.ClausesCopiedOut);
            // ANTI-VACUITY: it really did retract them all.
            Assert.False(e.Query("cp(_, _).").Success);
        }
    }

    /// <summary>The window is a VIEW, not a shortcut: a foreign retract that
    /// lands inside it forces the copy, and the enumeration still delivers
    /// the clause that was removed behind its back. This is the ISO logical
    /// update view, and it is the behaviour the eager copy had -- the
    /// enumeration below yields 1, 2, 3 and 4 even though 3 was retracted
    /// from under it after the first solution.</summary>
    [Fact]
    public void AForeignRetractInsideTheWindowCopiesItOutAndTheViewSurvives()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(p/1).
            p(1). p(2). p(3). p(4).
            seen(X) :- retract(p(X)),
                       ( X =:= 1 -> retract(p(3)) ; true ).
            """);
        var all = e.QueryAll("seen(X).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["X"]!).Value)
            .ToList();
        Assert.Equal(new long[] { 1, 2, 3, 4 }, all);
        // The copy actually happened -- otherwise this test proves nothing
        // about the materialize path.
        Assert.True(e.ClausesCopiedOut > 0);
        Assert.False(e.Query("p(_).").Success);
    }

    /// <summary>An assert during an open enumeration: appending is outside
    /// the window and must not join it (the view is fixed at call time),
    /// while prepending shifts it without disturbing what it holds. Neither
    /// one may cost a copy.</summary>
    [Fact]
    public void AssertsAroundAnOpenWindowDoNotJoinItOrCopyIt()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(q/1).
            q(1). q(2).
            grow(X) :- retract(q(X)),
                       ( X =:= 1 -> assertz(q(99)), asserta(q(0)) ; true ).
            """);
        var all = e.QueryAll("grow(X).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["X"]!).Value)
            .ToList();
        // Only the call-time view is enumerated: 99 was asserted after the
        // call began and 0 was prepended after it, so neither appears.
        Assert.Equal(new long[] { 1, 2 }, all);
        Assert.Equal(0L, e.ClausesCopiedOut);
        // Both survive the enumeration.
        Assert.Equal(2, e.QueryAll("q(_).").Count());
    }

    /// <summary>The enumeration is still an enumeration: every matching
    /// clause, in clause order, and the ones that do not match stay.</summary>
    [Fact]
    public void TheEnumerationStillYieldsEveryMatchInClauseOrder()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(r/2).
            r(a, 1). r(b, 2). r(a, 3). r(c, 4). r(a, 5).
            """);
        var got = e.QueryAll("retract(r(a, N)).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["N"]!).Value)
            .ToList();
        Assert.Equal(new long[] { 1, 3, 5 }, got);
        Assert.Equal(2, e.QueryAll("r(_, _).").Count());
    }
}

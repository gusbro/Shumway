using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>retract/1 left a choice point whenever any clause FOLLOWED the one
/// it matched, whether or not that clause could ever match. For the common
/// shape -- retract a keyed fact from a predicate keyed on that argument --
/// that meant every call left a choice point nothing could ever use, and every
/// one of them owed a view of the rest of the predicate. Each retract then
/// copied that view out when the next retract disturbed it: O(clauses) per
/// call, and quadratic over the loop.
///
/// <para>The first-argument index answers the real question -- is there a
/// further CANDIDATE, not is there a further clause -- so the call is simply
/// deterministic and there is no view to keep. Fewer choice points is also the
/// better answer: the clause that cannot match was never a solution.</para></summary>
public sealed class RetractDeterminismTests
{
    private const string Program = """
        :- dynamic(tok/1).
        mk(0) :- !.
        mk(N) :- assertz(tok(N)), M is N - 1, mk(M).
        drain(0) :- !.
        drain(N) :- retract(tok(N)), M is N - 1, drain(M).
        """;

    /// <summary>COUNTED, not timed. The loop has no cut, so every retract's
    /// choice point survives to the end of the query -- and each one used to
    /// cost a copy of everything still ahead of it. Now nothing is copied,
    /// because nothing is owed.</summary>
    [Fact]
    public void AKeyedRetractInALoopOwesNothing()
    {
        foreach (int n in new[] { 2_000, 8_000 })
        {
            var e = new PrologEngine { Out = new StringWriter() };
            e.ConsultString(Program);
            Assert.True(e.Query($"mk({n}).").Success);
            e.ClausesCopiedOut = 0;
            Assert.True(e.Query($"drain({n}).").Success);
            Assert.Equal(0L, e.ClausesCopiedOut);
            Assert.False(e.Query("tok(_).").Success);
        }
    }

    /// <summary>ANTI-VACUITY: a retract that really does have another
    /// candidate still leaves its choice point and still enumerates. Dropping
    /// one would be a solution that never runs, which is the only way this
    /// optimisation can be wrong.</summary>
    [Fact]
    public void ARetractWithMoreCandidatesStillEnumerates()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(m/2).
            m(k, 1). m(other, 2). m(k, 3). m(k, 4).
            """);
        // Three clauses share the key and all three must come back, in order.
        var got = e.QueryAll("retract(m(k, V)).")
            .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["V"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 3, 4 }, got);
        Assert.Single(e.QueryAll("m(_, _)."));
    }

    /// <summary>A clause whose first argument rules nothing out is a candidate
    /// for every key, so a retract followed by one is NOT deterministic even
    /// when no other clause shares its key.</summary>
    [Fact]
    public void AGeneralClauseAfterTheMatchKeepsTheChoicePoint()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(g/2).
            g(k, first).
            g(_, general).
            """);
        var got = e.QueryAll("retract(g(k, V)).")
            .Select(s => ((Shumway.Compiler.Ast.AtomTerm)s["V"]!).Name).ToList();
        Assert.Equal(new[] { "first", "general" }, got);
    }

    /// <summary>An unkeyable pattern proves nothing about what follows, so it
    /// keeps the old behaviour: a choice point whenever any clause follows.</summary>
    [Fact]
    public void AnUnkeyablePatternStillEnumeratesEverything()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(u/1).
            u(1). u(2). u(3).
            """);
        Assert.Equal(3, e.QueryAll("retract(u(_)).").Count());
        Assert.False(e.Query("u(_).").Success);
    }

    /// <summary>The last candidate for a key is deterministic even with other
    /// keys after it, and the enumeration still ends rather than failing
    /// early: backtracking into an exhausted retract must simply fail.</summary>
    [Fact]
    public void BacktrackingIntoAnExhaustedRetractFails()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(t/2).
            t(a, 1). t(b, 2). t(c, 3).
            """);
        Assert.Single(e.QueryAll("retract(t(a, _))."));
        // The retracted one is gone, the others are untouched and in order.
        Assert.Equal(new[] { "b", "c" },
            e.QueryAll("t(K, _).")
                .Select(s => ((Shumway.Compiler.Ast.AtomTerm)s["K"]!).Name));
        Assert.False(e.Query("retract(t(a, _)).").Success);
    }
}

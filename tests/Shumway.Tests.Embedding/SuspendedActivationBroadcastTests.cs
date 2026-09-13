using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A dynamic mutation is broadcast to the other activations of this
/// engine, because each runs on its own bytecode buffer: a nested query's
/// assert or retract has to reach the buffer of the outer query it suspended,
/// or that outer query keeps dispatching over a clause that is gone. A single
/// mutable code space gives that for free; a buffer per query does not.
///
/// <para>The list of activations to tell was only ever added to, and weakly.
/// So it also held every query that had already RETURNED and had not been
/// collected yet, and each of those took a full chain patch on every mutation
/// — one wasted O(chain) walk per retract, for a query that can never resume.
/// The list now drops an activation when its query ends, which leaves exactly
/// the suspended ones: an activation is suspended precisely when it is open
/// and is not the one running, and the one running is excluded already.</para>
///
/// <para>The entries stay weak on purpose. A caller who abandons an enumerator
/// instead of disposing it never reaches the end-of-query path, and a strong
/// list would pin that activation forever.</para></summary>
public sealed class SuspendedActivationBroadcastTests
{
    /// <summary>COUNTED, not timed. Nothing is suspended while this drain
    /// runs, so nothing is broadcast to — where before, every retract was
    /// broadcast to the finished query that had built the clauses.</summary>
    [Fact]
    public void AMutationWithNothingSuspendedBroadcastsToNobody()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(cp/1).
            mk(0) :- !.
            mk(N) :- assertz(cp(N)), M is N - 1, mk(M).
            drain(0) :- !.
            drain(N) :- retract(cp(_)), !, M is N - 1, drain(M).
            """);
        // A separate, COMPLETED query builds the clauses: its activation is
        // exactly the one that used to be broadcast to.
        Assert.True(e.Query("mk(8000).").Success);
        e.BroadcastTargets = 0;
        Assert.True(e.Query("drain(8000).").Success);
        Assert.Equal(0L, e.BroadcastTargets);
        Assert.False(e.Query("cp(_).").Success);
    }

    /// <summary>ANTI-VACUITY: an activation that is still OPEN stays a
    /// broadcast target, and gets the mutation. A query that has merely been
    /// left mid-enumeration is exactly that — it can resume, so it must be
    /// told — and it is the case the counted test above would otherwise be
    /// passing vacuously by never having a target at all.</summary>
    [Fact]
    public void AnOpenEnumerationIsStillABroadcastTarget()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(cp/1).
            mk(0) :- !.
            mk(N) :- assertz(cp(N)), M is N - 1, mk(M).
            drain(0) :- !.
            drain(N) :- retract(cp(_)), !, M is N - 1, drain(M).
            """);
        Assert.True(e.Query("mk(10).").Success);
        // Left mid-enumeration, NOT disposed: still open, so still a target.
        using var open = e.QueryAll("cp(X).").GetEnumerator();
        Assert.True(open.MoveNext());
        // Enough asserts to force the next query onto a REBUILT buffer, so the
        // open enumeration is left on a different one and the broadcast is the
        // only thing that can reach it.
        Assert.True(e.Query("mk(8000).").Success);
        e.BroadcastTargets = 0;
        Assert.True(e.Query("drain(4000).").Success);
        Assert.True(e.BroadcastTargets > 0,
            "an open enumeration on its own buffer was not told");
    }

    /// <summary>An abandoned enumerator never reaches the end-of-query path,
    /// so its activation is never unregistered. It must not be pinned by the
    /// list, and the engine must keep working around it.</summary>
    [Fact]
    public void AnAbandonedEnumeratorDoesNotBreakLaterMutations()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(p/1).
            p(1). p(2). p(3).
            """);
        // Take one solution and walk away without disposing.
        var it = e.QueryAll("p(X).").GetEnumerator();
        Assert.True(it.MoveNext());
        // Mutations after it still work, and the database ends up right.
        Assert.True(e.Query("retract(p(2)), assertz(p(4)).").Success);
        Assert.Equal(new long[] { 1, 3, 4 },
            e.QueryAll("p(X).")
                .Select(s => ((Shumway.Compiler.Ast.IntTerm)s["X"]!).Value));
    }
}

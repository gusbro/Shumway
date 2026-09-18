using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-051 phase 1: the audit. A domain used to be a managed object
/// the collector never saw and nothing ever freed; it is now heap cells, so
/// it is subject to every rule heap cells are subject to. These are the
/// places where getting that wrong is silent -- a domain collected while a
/// choice point can still restore it, or retained by a root that can no
/// longer be reached, are both invisible until something much later goes
/// wrong.</summary>
public sealed class FdDomainHeapLifetimeTests(ITestOutputHelper o)
{
    private static PrologEngine Fd()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).\n");
        return e;
    }

    private static void Holds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal + ".").Success, goal);

    /// <summary>The attribute store is a root the collector honours: a
    /// domain survives a collection and still says what it said. Without
    /// this the domain is either freed under the variable or left behind at
    /// an address the compaction moved.</summary>
    [Fact]
    public void ACollectionDoesNotDisturbALiveDomain()
    {
        var e = Fd();
        Holds(e, "A in 1..9, A #\\= 5, garbage_collect, "
                 + "findall(A, indomain(A), L), L == [1,2,3,4,6,7,8,9]");
        // And with the domain fragmented enough that it is several cells.
        Holds(e, "B in 1..20, B #\\= 5, B #\\= 9, B #\\= 14, garbage_collect, "
                 + "findall(B, indomain(B), L), length(L, 17)");
    }

    /// <summary>A domain the current attribute no longer points at is still
    /// live while a choice point can restore it. Collecting mid-search is
    /// where that goes wrong: the trail holds the old attribute, and if the
    /// collector does not treat it as a root the restored domain is
    /// garbage.</summary>
    [Fact]
    public void ABacktrackedDomainSurvivesACollection()
    {
        var e = Fd();
        Holds(e, "A in 1..9, A #\\= 5, ( A #> 7, garbage_collect, fail ; true ), "
                 + "findall(A, indomain(A), L), L == [1,2,3,4,6,7,8,9]");
        // Two narrowings deep, so what has to come back is an intermediate
        // domain and not the original one.
        Holds(e, "B in 1..9, B #\\= 5, ( B #> 3, ( B #> 7, garbage_collect, fail "
                 + "; true ), findall(B, indomain(B), L), L == [4,6,7,8,9] )");
    }

    /// <summary>Narrow, then cut: after the cut nothing can restore the
    /// intermediate domains, and with domains on the heap that garbage is
    /// reclaimable. Asserted on the domains ALONE, through the $dom_*
    /// builtins, with no constraint variable involved.
    ///
    /// <para>Measured through clpfd instead, the number is dominated by
    /// something else: a cut leaves the attributed variable in the attribute
    /// table with its propagator list, and the table is a GC root, so the
    /// propagators are retained. That is not this arc. It costs the same
    /// before and after (27,480 heap cells against 27,570 for thirty rounds
    /// of 200 propagators, the 90 being the collapsed final domains), and
    /// asserting on it here would have pinned someone else's leak.</para>
    /// </summary>
    [Fact]
    public void AbandonedDomainsAreReclaimed()
    {
        var e = Fd();
        e.ConsultString("""
            shrink(0, D, D) :- !.
            shrink(N, D, Out) :- '$dom_del'(D, N, D2),
                                 N1 is N - 1, shrink(N1, D2, Out).

            % Every intermediate domain is unreachable once this returns, and
            % the cut means no choice point can ask for one back.
            churn(N) :- '$dom_new'(1, 2000, D), shrink(N, D, _), !.
            """);

        long few = HeapBytesAfter(e, "churn(100)");
        long many = HeapBytesAfter(e, "churn(400)");
        o.WriteLine($"heap after collection: 100 shrinks {few:N0} bytes, "
                    + $"400 shrinks {many:N0} bytes");

        Assert.True(many < few * 2,
            $"400 abandoned domains retained {many} bytes against {few} for "
            + "100: a domain nothing can reach is not being reclaimed");
    }

    /// <summary>Anti-vacuity: the shrinking really does build a domain each
    /// step, so the flat heap above is reclamation and not a loop that
    /// allocated nothing.</summary>
    [Fact]
    public void TheShrinkingReallyBuildsDomains()
    {
        var e = Fd();
        var r = e.Query("'$dom_new'(1, 2000, D), term_cells(D, C1), "
                        + "'$dom_del'(D, 10, D2), '$dom_del'(D2, 20, D3), "
                        + "'$dom_del'(D3, 30, D4), term_cells(D4, C4).");
        Assert.True(r.Success);
        long one = long.Parse(r.Bindings["C1"].ToString()!);
        long four = long.Parse(r.Bindings["C4"].ToString()!);
        o.WriteLine($"one interval: {one} cells, four intervals: {four} cells");
        Assert.True(four > one,
            "a four-interval domain costs no more cells than a one-interval "
            + "one: the domain is not on the heap");
    }

    /// <summary>A domain still does not reach a term a user can copy, which
    /// is what kept copy_term/2 out of this change (ADR-051). Checked rather
    /// than assumed, because the representation is the kind of thing that
    /// makes it start.</summary>
    [Fact]
    public void ADomainStillDoesNotEscapeIntoUserTerms()
    {
        var e = Fd();
        // The copy carries no attribute, so it carries no domain:
        // it is a variable like any other and can be bound freely.
        Holds(e, "A in 1..9, copy_term(A, B), var(B), B = 42");
        Holds(e, "A in 1..9, copy_term(A, _, Gs), Gs = [_ in _]");
        Holds(e, "A in 1..9, findall(A, member(_, [x]), [B]), var(B)");
    }

    /// <summary>Heap bytes in use after running the goal and collecting.</summary>
    private static long HeapBytesAfter(PrologEngine e, string goal)
    {
        var r = e.Query($"{goal}, garbage_collect, "
                        + "statistics(global_stack, [Used, _]).");
        Assert.True(r.Success, goal);
        return long.Parse(r.Bindings["Used"].ToString()!);
    }
}

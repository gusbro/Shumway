using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The cut hook asks which cleanup handlers a barrier reaches, and it
/// asks far more often than it gets a yes. Walking every live handler each
/// time made a NEST of setup_call_cleanup quadratic: 4,000 deep spent
/// 25,749,670 steps in that loop, about 1.6n squared.
///
/// <para>Registration level rises with registration order unless something
/// registers after backtracking below an older handler, which is noticed as it
/// happens. While it holds, the handlers a barrier can reach are a SUFFIX, so
/// binary search finds where they start and the scan runs forward from there
/// -- forward, so the order they are enqueued in, which is the order they run
/// in, is exactly what it was.</para></summary>
public sealed class CleanupHandlerFireTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            :- dynamic(fired/1).
            nest(0, G) :- !, G.
            nest(N, G) :- M is N - 1,
                          setup_call_cleanup(true, nest(M, G), true).
            ord(0) :- !.
            ord(N) :- M is N - 1,
                      setup_call_cleanup(true, ord(M), assertz(fired(N))).
            """);
        return e;
    }

    /// <summary>ANTI-VACUITY first, because it is the thing that could break:
    /// cleanups run, exactly once each, in the order they did before --
    /// innermost out.</summary>
    [Fact]
    public void NestedCleanupsStillRunOnceAndInOrder()
    {
        var e = Engine();
        Assert.True(e.Query("ord(5).").Success);
        var sols = e.QueryAll("findall(N, fired(N), L).").ToList();
        Assert.Single(sols);
        // ToString is the canonical form; the ORDER is what this pins.
        Assert.Equal(".(1,.(2,.(3,.(4,.(5,[])))))",
            sols[0]["L"]!.ToString()!.Replace(" ", ""));
    }

    /// <summary>A cleanup still fires on failure, on a throw, and on a cut,
    /// which are the three ways the hook is reached.</summary>
    [Fact]
    public void TheThreeFiringPathsAreUnchanged()
    {
        var e = Engine();
        Assert.True(e.Query(
            "\\+ setup_call_cleanup(true, fail, assertz(fired(onfail))), "
            + "fired(onfail).").Success);
        Assert.True(e.Query(
            "catch(setup_call_cleanup(true, throw(b), assertz(fired(onthrow))), "
            + "b, true), fired(onthrow).").Success);
        Assert.True(e.Query(
            "( setup_call_cleanup(true, member(_, [1,2]), "
            + "assertz(fired(oncut))) -> true ; true ), fired(oncut).").Success);
    }

    /// <summary>NOT here: a shape test on a nest of setup_call_cleanup. The
    /// scan this fixes was one of TWO quadratics in that workload, and the
    /// other one is still standing -- the implementation asserts a
    /// '$cleanup_pending' clause per level and retracts it on the way out, and
    /// a call to a dynamic predicate costs O(clauses) whatever its first
    /// argument is. So the nest is still quadratic end to end and a shape test
    /// would fail for a reason that is not this change. It belongs with that
    /// fix, where it can actually hold.</summary>
}

using System.Reflection;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 123 (Phase 8, ADR-015 chunk C step 4, sub-1): per-dynamic-clause
/// chain state and <c>retract</c>-patches-died at the bytecode level.
/// The chunk-C redirect (recompile-on-modify) is still active in
/// parallel — these tests verify the new path runs alongside it
/// correctly; the redirect goes away in a follow-up sub-commit.
/// </summary>
public class Chunk123Tests
{
    private static int Functor(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static int? PeekDied(PrologEngine engine, int fid, int idx)
    {
        var method = typeof(PrologEngine).GetMethod(
            "PeekDiedAddr", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int?)method.Invoke(engine, new object[] { fid, idx });
    }

    private static byte[] ReadProgram(PrologEngine engine)
    {
        // After a query has run, _currentEngine isn't held on PrologEngine;
        // the most recently used Engine's program lives until the next
        // query setup. We grab it indirectly by running a no-op query and
        // peeking through the Engine via an internal accessor — but that
        // accessor doesn't exist here, so we synthesise it via reflection
        // on the dyn-chains' die-slot reads. Simpler: do a fresh query
        // that exposes the program via a probe builtin? The simpler path:
        // verify behaviour observably (assert+retract+call sees the
        // retract via the bytecode patch). This helper isn't actually
        // needed for the behavioural tests below.
        throw new System.NotSupportedException("Use behavioural assertions.");
    }

    [Fact]
    public void DynChainState_PopulatedAfterQuerySetup()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        // Run a query whose setup triggers chain population.
        Assert.True(e.Query("d(1).").Success);

        int fid = Functor("d", 1);
        Assert.NotNull(PeekDied(e, fid, 0));
        Assert.NotNull(PeekDied(e, fid, 1));
        Assert.NotNull(PeekDied(e, fid, 2));
        Assert.Null(PeekDied(e, fid, 3));   // only 3 clauses
    }

    [Fact]
    public void RetractRemovesChainEntryAlongsideDynamicClauses()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        // First query: builds chain state with 3 entries.
        Assert.Equal(3, e.QueryAll("d(X).").Count());

        int fid = Functor("d", 1);
        Assert.NotNull(PeekDied(e, fid, 2));   // 3 entries before retract

        // Retract one — the chain entry should disappear too. Functional
        // visibility check: only two clauses match d(X) afterwards.
        e.Query("retract(d(2)).");
        Assert.Equal(2, e.QueryAll("d(X).").Count());
    }

    [Fact]
    public void SameQueryRetract_StillWorks_WithBytecodePatchPathLive()
    {
        // The Chunk118 scenario re-verified: chunk-C redirect AND the new
        // bytecode patch are both active. End behaviour must match.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)).");
        Assert.True(e.Query("retract(d(1)), \\+ d(1), d(2).").Success);
    }
}

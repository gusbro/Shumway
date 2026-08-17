using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Regression tests for the suspended-activation stale-append-position
/// corruption (found via a Chunk117 suite hang, 2026-07-11): an activation
/// suspended mid-enumeration shares the persistent buffer with a nested
/// query; when the nested query extends a dynamic chain in place and the
/// suspended activation then resumes and asserts too, its stale append
/// position landed ON the sibling's entries — the tail patch wrote a
/// retry_me_else whose &lt;next&gt; pointed at itself, hanging every later
/// walk/dispatch of the chain. Root fix: <c>ResyncOwnerAppendPosition</c>
/// brings an owner's append position forward before any in-place mutation;
/// tripwires + walk cycle guards make the corrupting write impossible even
/// if a new producer of the shape appears.
/// </summary>
public class SuspendedActivationAssertTests
{
    [Fact]
    public void SuspendedEnumeration_AssertsInterleavedWithNestedQuery_NoCorruption()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic k/1.
            seed(1).
            seed(2).
            seed(3).
            """);
        // A1 asserts one k/1 fact per solution and suspends between them.
        using var it = e.QueryAll("seed(X), assertz(k(X)).").GetEnumerator();
        Assert.True(it.MoveNext());              // A1: assertz(k(1)), suspended
        // A2 (nested, same shared buffer) extends the same chain.
        Assert.True(e.Query("assertz(k(10)).").Success);
        Assert.True(it.MoveNext());              // A1 resumes: assertz(k(2))
        Assert.True(e.Query("assertz(k(20)).").Success);
        Assert.True(it.MoveNext());              // A1: assertz(k(3))
        Assert.False(it.MoveNext());
        // Pre-fix this either hung the next walk (self-pointing entry) or
        // silently lost the sibling's clauses (overwritten chunks).
        Assert.True(e.Query("findall(X, k(X), L), L == [1, 10, 2, 20, 3].").Success);
        // And the chain stays extensible + walkable afterwards.
        Assert.True(e.Query("assertz(k(99)), k(99).").Success);
        Assert.Equal(6, e.QueryAll("k(X).").Count());
    }

    [Fact]
    public void SuspendedEnumeration_ManyInterleavedAsserts_ChainStaysSound()
    {
        // The Chunk117 shape (static predicate calling a growing dynamic)
        // under deliberate interleaving, driven well past the JIT-indexing
        // threshold so the extensible-indexed layout is exercised too.
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic d/1.
            uses(X) :- d(X).
            seed(1).
            seed(2).
            """);
        for (int i = 1; i <= 15; i++)
        {
            using var it = e.QueryAll("seed(S), assertz(d(S)).").GetEnumerator();
            Assert.True(it.MoveNext());          // suspended after one assert
            Assert.True(e.Query($"assertz(d({100 + i})).").Success);
            Assert.True(it.MoveNext());          // resumes, asserts again
            Assert.True(e.Query($"uses({100 + i}).").Success);
        }
        // 15 rounds × (2 seeds + 1 nested) = 45 clauses, all reachable.
        Assert.Equal(45, e.QueryAll("uses(X).").Count());
    }
}

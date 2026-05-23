using System.Reflection;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 127 (Phase 8, ADR-015 chunk C step 4, sub-2d): incremental
/// <c>assertz</c> — compile one clause and patch the chain's tail
/// <c>&lt;next&gt;</c> in place, instead of recompiling the whole
/// predicate. The chunk-C redirect still fires alongside for safety
/// (sub-2e removes it), so these tests verify both: the chain-state
/// data after the incremental append, and the end-to-end behaviour.
/// </summary>
public class Chunk127Tests
{
    private static int Functor(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static int? Peek(string m, PrologEngine engine, int fid, int idx)
    {
        var method = typeof(PrologEngine).GetMethod(
            m, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int?)method.Invoke(engine, new object[] { fid, idx });
    }

    private static int? PeekTail(PrologEngine engine, int fid)
    {
        var method = typeof(PrologEngine).GetMethod(
            "PeekTailNextAddr", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int?)method.Invoke(engine, new object[] { fid });
    }

    [Fact]
    public void AssertzOnEmptyDynamic_AppendsAndLinksFromStubTail()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        // Force chain population: the declared-but-empty d/1 has just the
        // empty-stub clause; its try_me_else <fail-stub> is the tail.
        e.Query("true.");

        int fid = Functor("d", 1);
        int? tailBefore = PeekTail(e, fid);
        Assert.NotNull(tailBefore);
        Assert.True(tailBefore > 0);

        // Incremental assertz appends a chunk; the chain's tail advances.
        e.Query("assertz(d(1)).");

        int? tailAfter = PeekTail(e, fid);
        Assert.NotNull(tailAfter);
        Assert.NotEqual(tailBefore, tailAfter);

        // The new clause has a chain entry.
        Assert.NotNull(Peek("PeekDiedAddr", e, fid, 0));
        Assert.NotNull(Peek("PeekNextAddr", e, fid, 0));
    }

    [Fact]
    public void RepeatedAssertz_ExtendsChainEntryByEntry()
    {
        // Assert three clauses in a single query — chain grows from 0 to
        // 3 within the same program, so each clause's next-operand
        // position is genuinely distinct (cross-query addresses can
        // coincidentally repeat when layouts are identical).
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query(
            "assertz(d(1)), assertz(d(2)), assertz(d(3)).").Success);
        int fid = Functor("d", 1);

        // Three chain entries, each with its own died and next addresses.
        Assert.NotNull(Peek("PeekDiedAddr", e, fid, 0));
        Assert.NotNull(Peek("PeekDiedAddr", e, fid, 1));
        Assert.NotNull(Peek("PeekDiedAddr", e, fid, 2));
        var n0 = Peek("PeekNextAddr", e, fid, 0);
        var n1 = Peek("PeekNextAddr", e, fid, 1);
        var n2 = Peek("PeekNextAddr", e, fid, 2);
        Assert.NotEqual(n0, n1);
        Assert.NotEqual(n1, n2);
        Assert.NotEqual(n0, n2);

        // Tail still points past the last clause.
        Assert.Equal(n2, PeekTail(e, fid));

        // End-to-end visibility.
        Assert.Equal(3, e.QueryAll("d(X).").Count());
    }

    [Fact]
    public void IncrementalAssertz_FollowedByRetract_Works()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        e.Query("retract(d(2)).");
        Assert.Equal(2, e.QueryAll("d(X).").Count());
        Assert.True(e.Query("d(1), \\+ d(2), d(3).").Success);
    }

    [Fact]
    public void SameQueryAssertThenCall_StillWorks()
    {
        // The chunk-118 headline scenario: assertz then directly call in
        // the same query. Chunk-C redirect still active too — this just
        // verifies the new path didn't break it.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query("assertz(d(1)), d(1).").Success);
        Assert.True(e.Query("assertz(d(2)), d(2).").Success);
    }
}

using System.Reflection;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 128 (Phase 8, ADR-015 chunk C step 4, sub-2e): asserta is now
/// incremental too — via the trampoline + try_me_else-to-retry_me_else
/// in-place demotion (the +4-nops trick). Chunk-C's redirect machinery
/// is gone. These tests cover the asserta path end-to-end and verify
/// the chain-state invariants.
/// </summary>
public class Chunk128Tests
{
    private static int Functor(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static int? Peek(string m, PrologEngine e, int fid, int idx) =>
        (int?)typeof(PrologEngine).GetMethod(
            m, BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(e, new object[] { fid, idx });

    private static int? PeekHead(PrologEngine e, int fid) =>
        (int?)typeof(PrologEngine).GetMethod(
            "PeekHeadClauseAddr", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(e, new object[] { fid });

    [Fact]
    public void AssertaOnEmptyDynamic_InstallsAsHead()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query("asserta(d(7)), d(7).").Success);
    }

    [Fact]
    public void AssertaPrependsBeforeExisting()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query(
            "assertz(d(2)), assertz(d(3)), asserta(d(1)).").Success);
        // Enumeration order: asserta puts d(1) at the front.
        var xs = e.QueryAll("d(X).").Select(s => s["X"]!.ToString()).ToList();
        Assert.Equal(new[] { "1", "2", "3" }, xs);
    }

    [Fact]
    public void AssertaThenAssertz_OrderingPreserved()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query(
            "asserta(d(2)), asserta(d(1)), assertz(d(3)), assertz(d(4)).").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]!.ToString()).ToList();
        Assert.Equal(new[] { "1", "2", "3", "4" }, xs);
    }

    [Fact]
    public void AssertaUpdatesHeadClauseAddr()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(2)).");
        int fid = Functor("d", 1);
        int? headBefore = PeekHead(e, fid);

        e.Query("asserta(d(1)).");
        int? headAfter = PeekHead(e, fid);

        Assert.NotNull(headBefore);
        Assert.NotNull(headAfter);
        Assert.NotEqual(headBefore, headAfter);     // a new chunk became head
    }

    [Fact]
    public void RetractAfterAssertaSequence_StillWorks()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query(
            "assertz(d(2)), asserta(d(1)), assertz(d(3)), retract(d(2)).").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]!.ToString()).ToList();
        Assert.Equal(new[] { "1", "3" }, xs);
    }

    [Fact]
    public void AbolishViaChain_PatchesAllDied()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        // After abolish the predicate is UNDEFINED — the mid-query call
        // raises existence_error (§8.9.4), it does not fail over the
        // patched-dead chain.
        Assert.True(e.Query(
            "assertz(d(1)), assertz(d(2)), assertz(d(3)), " +
            "abolish(d/1), " +
            "catch(d(_), error(existence_error(procedure, d/1), _), true).").Success);
    }
}

using System.Reflection;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 126 (Phase 8, ADR-015 chunk C step 4, sub-2c): per-clause chain
/// state now also tracks each clause's chain-instruction
/// <c>&lt;next&gt;</c> operand position — the address an upcoming
/// incremental <c>assertz</c> will patch in place to link a freshly
/// compiled clause onto the chain.
/// </summary>
public class Chunk126Tests
{
    private static int Functor(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static int? Peek(string method, PrologEngine engine, int fid, int idx)
    {
        var m = typeof(PrologEngine).GetMethod(
            method, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int?)m.Invoke(engine, new object[] { fid, idx });
    }

    [Fact]
    public void SingleClauseDynamic_HasTryMeElseNextOperand()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        Assert.True(e.Query("d(1).").Success);

        int fid = Functor("d", 1);
        int? next = Peek("PeekNextAddr", e, fid, 0);
        Assert.NotNull(next);
        Assert.True(next > 0);
    }

    [Fact]
    public void MultiClauseDynamic_EachClauseHasItsNextOperand()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        Assert.Equal(3, e.QueryAll("d(X).").Count());

        int fid = Functor("d", 1);
        // Every clause has a chain instruction in front (try_me_else for
        // the first, retry_me_else for the rest including the last —
        // sub-2b emits retry_me_else <fail-stub> for last).
        Assert.NotNull(Peek("PeekNextAddr", e, fid, 0));
        Assert.NotNull(Peek("PeekNextAddr", e, fid, 1));
        Assert.NotNull(Peek("PeekNextAddr", e, fid, 2));

        // No fourth clause — the chain state is bounded by the live set.
        Assert.Null(Peek("PeekNextAddr", e, fid, 3));
    }

    [Fact]
    public void EachClauseAlsoStillTracksItsDiedOperand()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)), assertz(d(2)).");
        Assert.Equal(2, e.QueryAll("d(X).").Count());

        int fid = Functor("d", 1);
        Assert.NotNull(Peek("PeekDiedAddr", e, fid, 0));
        Assert.NotNull(Peek("PeekDiedAddr", e, fid, 1));
        Assert.NotNull(Peek("PeekNextAddr", e, fid, 0));
        Assert.NotNull(Peek("PeekNextAddr", e, fid, 1));
    }
}

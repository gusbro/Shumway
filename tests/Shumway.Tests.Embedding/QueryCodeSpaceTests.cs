using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A query's code is appended to the code buffer after the persistent
/// program (or into a hole an earlier query left) and given back when the query
/// ends; what the query appends while it runs follows it. The buffer has one
/// limit, <see cref="PrologEngine.CodeSpaceLimit"/>, and holes count as room.
/// Logtalk's mutation_testing once grew a query's additions into the query's
/// own code.</summary>
public class QueryCodeSpaceTests
{
    private const string Filler = @"
        :- dynamic(f/1).
        fill(I, Max, N) :-
            (   I > Max -> N = Max
            ;   catch(assertz(f(I)), error(resource_error(code_space), _), Full = true),
                (   Full == true -> N is I - 1
                ;   I1 is I + 1, fill(I1, Max, N)
                )
            ).
        sum([], S, S).
        sum([X|Xs], S0, S) :- S1 is S0 + X, sum(Xs, S1, S).
        intact(N) :-
            findall(X, f(X), Xs), length(Xs, N),
            sum(Xs, 0, S), S =:= N * (N + 1) // 2.
    ";

    [Fact]
    public void AnAssertPastTheLimitIsAResourceErrorAndLeavesTheClausesIntact()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Filler);
        Assert.True(engine.Query("true.").Success);
        engine.CodeSpaceLimit = engine._persistentLength + 256 * 1024;

        // The limit is reached well inside the bound, the clauses asserted
        // before it all run, and the refused one is not there.
        Assert.True(engine.Query(@"
            fill(1, 100000, N), N > 1000, N < 100000,
            intact(N), nb_setval(filled, N).").Success);

        // The next query sees the same clauses; the room is still gone.
        Assert.True(engine.Query(@"
            nb_getval(filled, N), intact(N),
            catch(assertz(f(0)), error(resource_error(code_space), _), true), \+ f(0).").Success);

        // Retracting them and reclaiming their chunks makes a hole, which is room.
        Assert.True(engine.Query("retract(f(_)), fail ; true.").Success);
        Assert.True(engine.Query("garbage_collect_clauses, assertz(f(0)), f(0).").Success);
    }

    [Fact]
    public void TheQueryCodeFollowsThePersistentProgramAndGoesWithTheQuery()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(1). p(2).");
        Assert.True(engine.Query("p(1).").Success);
        int before = engine._persistentLength;

        var solutions = engine.QueryAll("p(X).").GetEnumerator();
        Assert.True(solutions.MoveNext());
        Assert.True(engine._persistentLength > before, "the query's code was not appended");
        Assert.True(solutions.MoveNext());
        Assert.False(solutions.MoveNext());
        solutions.Dispose();

        Assert.Equal(before, engine._persistentLength);
    }

    [Fact]
    public void AQueryThatAppendsLeavesItsCodeAsAHoleTheNextQueryFills()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic(k/1).");
        Assert.True(engine.Query("true.").Success);
        int l0 = engine._persistentLength;
        Assert.True(engine.Query("assertz(k(1)).").Success);
        int l1 = engine._persistentLength;
        Assert.True(engine.Query("assertz(k(2)).").Success);
        int l2 = engine._persistentLength;
        Assert.True(engine.Query("assertz(k(3)).").Success);
        int l3 = engine._persistentLength;

        // The first query's code stays as a hole below its clause; the next
        // queries' code lands in it, so from then on only the clauses add up.
        Assert.True(l1 - l0 > l2 - l1, $"the first query grew the buffer by {l1 - l0}, the second by {l2 - l1}");
        Assert.Equal(l2 - l1, l3 - l2);
        Assert.True(engine.Query("findall(X, k(X), L), L == [1, 2, 3].").Success);
    }

    [Fact]
    public void TheLimitIsAdjustableAndAQueryWithNoRoomForItsCodeIsAResourceError()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(1).");
        Assert.True(engine.Query("p(1).").Success);
        engine.CodeSpaceLimit = engine._persistentLength;

        var ex = HostError.Raised(() => engine.Query("p(1)."));
        Assert.Equal("resource_error", ex.Kind);
        Assert.Equal("code_space", ex.Detail);

        engine.CodeSpaceLimit = PrologEngine.DefaultCodeSpaceLimit;
        Assert.True(engine.Query("p(1).").Success);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.CodeSpaceLimit = Activation.ResumeMarkerBase + 1);
    }

    [Fact]
    public void TheDefaultLimitDependsOnTheProcessBitness()
    {
        Assert.Equal(Environment.Is64BitProcess ? Activation.ResumeMarkerBase : 256 * 1024 * 1024,
            PrologEngine.DefaultCodeSpaceLimit);
    }
}

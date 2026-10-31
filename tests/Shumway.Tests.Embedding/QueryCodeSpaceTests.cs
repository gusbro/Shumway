using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The code a query adds while it runs (asserted clauses, consults)
/// goes below the query's own code. Logtalk's mutation_testing grew past the
/// room once, and later clauses ran the query's code instead.</summary>
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
    public void AnAssertPastTheRoomIsAResourceErrorAndLeavesTheClausesIntact()
    {
        var engine = new PrologEngine { TestQueryGrowthRoom = 256 * 1024 };
        engine.ConsultString(Filler);

        // The limit is reached well inside the bound, the clauses asserted
        // before it all run, and the refused one is not there.
        Assert.True(engine.Query(@"
            fill(1, 100000, N), N > 1000, N < 100000,
            intact(N), nb_setval(filled, N).").Success);

        // The next query sees the same clauses and has room again.
        Assert.True(engine.Query(@"
            nb_getval(filled, N), intact(N), assertz(f(0)).").Success);
    }

    [Fact]
    public void TheQueryCodeSitsJustBelowTheResumeMarkers()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(1).");
        Assert.True(engine.Query("p(1).").Success);

        Assert.True(engine._querySplit >= Activation.ResumeMarkerBase - 64 * 1024 * 1024);
        Assert.True(engine._querySplit < Activation.ResumeMarkerBase);
    }
}

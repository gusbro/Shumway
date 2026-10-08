using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A dynamic predicate's chain after its clauses come and go at the
/// head (a stack kept with <c>asserta/1</c> and <c>retract/1</c>, an unget
/// buffer): a sweep leaves no retired clause linked behind the head, or every
/// later call would walk them all. The observable is the chain itself: the
/// chunks its links pass through that hold no live clause.</summary>
public sealed class DynChainDeadHeadTests
{
    private static PrologEngine Engine(string program)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;   // the chain is the interpreter's
        e.ConsultString(program);
        e.ChainAudit = true;
        return e;
    }

    [Fact]
    public void AStackOfAssertaAndRetract_LeavesNoRetiredClauseLinked()
    {
        var e = Engine("""
            :- dynamic st/1.
            churn(0) :- !.
            churn(N) :- asserta(st(N)), retract(st(N)), M is N - 1, churn(M).
            """);
        Assert.True(e.Query("churn(400).").Success);
        // ANTI-VACUITY: the chain was swept many times.
        Assert.True(e.ChainReclaims >= 50, $"only {e.ChainReclaims} sweeps");
        Assert.InRange(e.ChainDeadLinkedMax, 0, 1);
    }

    [Fact]
    public void AStackOverALiveClause_LeavesNoRetiredClauseLinked()
    {
        var e = Engine("""
            :- dynamic st/1.
            churn(0) :- !.
            churn(N) :- asserta(st(N)), once(st(Top)), Top == N, retract(st(N)), M is N - 1, churn(M).
            """);
        Assert.True(e.Query("assertz(st(base)), churn(400).").Success);
        Assert.True(e.ChainReclaims >= 50, $"only {e.ChainReclaims} sweeps");
        Assert.InRange(e.ChainDeadLinkedMax, 0, 1);
        var all = e.QueryAll("st(X).").ToList();
        Assert.Single(all);
        Assert.Equal("base", $"{all[0]["X"]}");
    }

    // A consulted clause is the chain's head and stays live under the churn:
    // the sweep must not take it for a retired head and link past it.
    [Fact]
    public void AStackOverAConsultedClause_KeepsThatClause()
    {
        var e = Engine("""
            :- dynamic st/1.
            st(base).
            churn(0) :- !.
            churn(N) :- asserta(st(N)), retract(st(N)), M is N - 1, churn(M).
            """);
        Assert.True(e.Query("churn(40), garbage_collect_clauses, findall(X, st(X), L), L == [base].").Success);
        Assert.True(e.Query(
            "asserta(st(top)), churn(40), garbage_collect_clauses, findall(X, st(X), L), L == [top, base].").Success);
        Assert.True(e.Query(
            "retract(st(base)), churn(40), garbage_collect_clauses, findall(X, st(X), L), L == [top].").Success);
    }

    // The unget buffer: pushed at the head, appended at the tail, read by a
    // call that finds it empty most of the time.
    [Fact]
    public void AnUngetBuffer_KeepsItsOrderAndLeavesNoRetiredClauseLinked()
    {
        var e = Engine("""
            :- dynamic buf/1.
            unget(C) :- asserta(buf(C)).
            append_char(C) :- assertz(buf(C)).
            next(C) :- retract(buf(C)), !.
            churn(0) :- !.
            churn(N) :-
                unget(a(N)), append_char(z(N)), unget(b(N)),
                next(X), X == b(N), next(Y), Y == a(N), next(Z), Z == z(N),
                \+ buf(_),
                M is N - 1, churn(M).
            """);
        Assert.True(e.Query("churn(300).").Success);
        Assert.True(e.ChainReclaims >= 50, $"only {e.ChainReclaims} sweeps");
        Assert.InRange(e.ChainDeadLinkedMax, 0, 1);
        Assert.False(e.Query("buf(_).").Success);
    }
}

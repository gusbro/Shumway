using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-052: a row in the attribute table does not keep its variable
/// alive. The home is a weak key; the attribute value stays a strong
/// reference FROM a live home, so an attributed variable nothing can reach
/// is collected with its attributes, and one that IS reachable keeps them.
///
/// <para>Both directions are asserted here on purpose. "Nothing is retained"
/// is trivially satisfiable by collecting too much, and collecting an
/// attribute out from under a live variable is the one way this change can
/// turn a sound program into a wrong one.</para></summary>
public sealed class AttrTableWeakRootTests(ITestOutputHelper o)
{
    private static PrologEngine Fd()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).\n");
        return e;
    }

    // Fragments a domain with N propagators, then collapses it and cuts.
    // After the cut nothing in the program can name X: no choice point, no
    // trail entry that is not already dead, nothing on the stack.
    private const string Corpus = """
        frag(_, 0) :- !.
        frag(X, N) :- X #\= N, M is N - 1, frag(X, M).

        round :- X in 1..400, frag(X, 200), X #> 398, !.

        rounds(0) :- !.
        rounds(N) :- round, M is N - 1, rounds(M).
        """;

    /// <summary>Heap bytes in use after the goal and a full collection.</summary>
    private static long HeapBytesAfter(PrologEngine e, string goal)
    {
        var r = e.Query($"{goal}, garbage_collect, "
                        + "statistics(global_stack, [Used, _]).");
        Assert.True(r.Success, goal);
        return long.Parse(r.Bindings["Used"].ToString()!);
    }

    /// <summary>THE LEAK. Thirty rounds retained 54,570 of the 54,574 cells
    /// that survived a full collection, with no choice points and an empty
    /// binding trail: the attribute table held every one of them, because a
    /// row rooted its own variable. The heap therefore grew in proportion to
    /// the rounds for the life of the query.</summary>
    [Fact]
    public void AnAbandonedAttributedVariableIsCollectedWithItsAttributes()
    {
        var e = Fd();
        e.ConsultString(Corpus);
        long few = HeapBytesAfter(e, "rounds(5)");
        long many = HeapBytesAfter(e, "rounds(40)");
        o.WriteLine($"heap after collection: 5 rounds {few:N0} bytes, "
                    + $"40 rounds {many:N0} bytes");

        // Eight times the rounds. Retained, that is eight times the heap;
        // collected, it is flat. The bound sits between the two MEASURED
        // behaviours rather than at a tuned number.
        Assert.True(many < few * 2,
            $"40 rounds retained {many} bytes against {few} for 5: an "
            + "attributed variable nothing can reach is still pinned by its "
            + "own row in the attribute table");
    }

    /// <summary>ANTI-VACUITY for the test above: the rounds really do build
    /// the propagators whose absence it then measures. Without this, a
    /// clpfd that quietly stopped constraining would read as a fix.</summary>
    [Fact]
    public void TheRoundsReallyDoConstrain()
    {
        var e = Fd();
        e.ConsultString(Corpus);
        // The same shape as a round, but the variable is handed back: the
        // propagators narrowed it to exactly one value.
        // frag removes 1..200 and #> 398 leaves {399, 400}.
        Assert.True(e.Query("X in 1..400, frag(X, 200), "
                            + "X #> 398, findall(X, indomain(X), L), "
                            + "L == [399,400].").Success,
            "the rounds do not actually constrain: the leak test is vacuous");
    }

    /// <summary>THE COUNTER-PROOF, and the reason the ephemeron edge exists:
    /// a variable reachable ONLY from a Y slot must keep its attributes
    /// across a collection.
    ///
    /// <para>With the home no longer a root but no edge from a live home to
    /// its attributes, this is exactly the case that breaks: the Y slot
    /// marks the variable, nothing marks its domain, and the collection
    /// frees the domain under a live constraint. It fails RED against step 1
    /// of ADR-052 alone.</para></summary>
    [Fact]
    public void AReachableAttributedVariableKeepsItsAttributesAcrossACollection()
    {
        var e = Fd();
        e.ConsultString("""
            churn(0) :- !.
            churn(N) :- numlist(1, 50, _), M is N - 1, churn(M).

            % X is created here and used only AFTER the collection, so
            % between the two it lives in a Y slot and nowhere else.
            probe(L) :- X in 1..9, X #\= 5,
                        churn(3000), garbage_collect,
                        findall(V, (V = X, indomain(V)), L).
            """);
        // Asserted in Prolog: the comparison is the engine's own ==, not a
        // rendering of the answer.
        Assert.True(e.Query("probe(L), L == [1,2,3,4,6,7,8,9].").Success,
            "the domain did not survive the collection intact");
    }

    /// <summary>The same in the other direction: a constraint posted before a
    /// collection still REFUSES what it should. A domain read as garbage
    /// could enumerate correctly and still have stopped rejecting.</summary>
    [Fact]
    public void AReachableAttributedVariableStillRejectsAfterACollection()
    {
        var e = Fd();
        e.ConsultString("""
            churn(0) :- !.
            churn(N) :- numlist(1, 50, _), M is N - 1, churn(M).
            refuses :- X in 1..9, X #\= 5, churn(3000), garbage_collect,
                       ( X = 5 -> fail ; true ).
            admits  :- X in 1..9, X #\= 5, churn(3000), garbage_collect, X = 6.
            """);
        Assert.True(e.Query("refuses.").Success,
            "X #\\= 5 stopped rejecting 5 after a collection");
        Assert.True(e.Query("admits.").Success,
            "X #\\= 5 started rejecting 6 after a collection");
    }

    /// <summary>A variable held by a CHOICE POINT rather than a frame: the
    /// other way an attributed variable stays reachable, and the one the
    /// conservative stack scan has to cover.</summary>
    [Fact]
    public void AnAttributedVariableHeldByAChoicePointSurvives()
    {
        var e = Fd();
        e.ConsultString("""
            churn(0) :- !.
            churn(N) :- numlist(1, 50, _), M is N - 1, churn(M).
            pick(1). pick(2).
            % The choice point on pick/1 sits between the constraint and the
            % collection, and X is live across it.
            held(X) :- X in 1..9, X #\= 5, pick(_), churn(2000),
                       garbage_collect, indomain(X).
            """);
        // Eight values of the domain, twice over (pick/1 has two clauses).
        Assert.True(e.Query("findall(X, held(X), L), length(L, 16).").Success,
            "a variable held across a choice point lost its domain");
    }

    /// <summary>call_residue_vars/2 across a forced collection. Snapshots
    /// hold homes as raw addresses and are relocated, never marked; a home
    /// whose variable died must be DROPPED, because RelocIndex on an
    /// unmarked index lands on whatever live cell took that place, and the
    /// snapshot would then answer for an unrelated variable.</summary>
    [Fact]
    public void ResidueVarsSurviveACollectionThatDropsDeadHomes()
    {
        var e = Fd();
        e.ConsultString("""
            churn(0) :- !.
            churn(N) :- numlist(1, 50, _), M is N - 1, churn(M).
            % dead/0 leaves abandoned attributed variables behind; the
            % collection drops their rows, and the snapshot must not come
            % back naming whatever landed on their addresses.
            dead :- Y in 1..9, Y #\= 5, !.
            inner(X) :- dead, churn(1500), garbage_collect, X in 1..3.
            """);
        // Exactly the one variable the goal constrained and left unbound.
        Assert.True(e.Query(
            "call_residue_vars(inner(X), Vs), Vs == [X].").Success,
            "the residue vars are not the one variable the goal constrained");
    }

    /// <summary>The attribute trail log keeps its promise: a variable whose
    /// attributes the collector could have dropped is restored on
    /// backtracking, because a live undo record roots the home.</summary>
    [Fact]
    public void BacktrackingRestoresAnAttributeAcrossACollection()
    {
        var e = Fd();
        e.ConsultString("""
            churn(0) :- !.
            churn(N) :- numlist(1, 50, _), M is N - 1, churn(M).
            % Narrow inside a choice point, collect, fail back out: the
            % original domain has to come back intact.
            restored(L) :- X in 1..9,
                           ( X #> 7, churn(2000), garbage_collect, fail
                           ; true ),
                           findall(V, (V = X, indomain(V)), L).
            """);
        Assert.True(e.Query("restored(L), L == [1,2,3,4,5,6,7,8,9].").Success,
            "the original domain did not come back after backtracking over "
            + "a collection");
    }
}

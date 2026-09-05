using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What a global variable stores. A non-backtrackable write has to
/// survive the backtracking it is defined to survive, and what was stored was
/// the CELL: a heap address. Once the heap unwound past the write, the address
/// held whatever came after it, so the read handed back another term's cells
/// as if they were the value.
///
/// <code>
/// ?- nb_setval(k, f(1,2)), fail ; true.
/// ?- nb_getval(k, X).
/// X = clpfd_bmul(X, _G6, _G6).
/// </code>
///
/// <para>Integers and atoms are their own cells, which is why the counter this
/// predicate is for never noticed. A compound, a float, a string, a bignum and
/// a rational all noticed: garbage, or a host exception no catch/3 could see.
/// A non-backtrackable write now stores a copy that owes the heap nothing, the
/// way SICStus's blackboard does ("A copy of Term is stored"), and the copy
/// carries no attributes ("those attributes are not stored") -- copy_term/3 is
/// what carries a constraint, and bb_put/2 uses it.</para></summary>
public sealed class GlobalVariableStorageTests
{
    private static void Holds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal).Success, goal);

    [Theory]
    [InlineData("f(1,2)")]
    [InlineData("[a,b,c]")]
    [InlineData("hola")]
    [InlineData("42")]
    [InlineData("1.5")]
    [InlineData("\"texto\"")]
    [InlineData("f(g(h), [1, 2.5, \"t\"])")]
    public void AValueSurvivesTheBacktrackingItIsMeantTo(string value)
    {
        var e = new PrologEngine();
        Holds(e, $"V = {value}, ( nb_setval(k, V), fail ; true ), "
               + $"nb_getval(k, X), X == {value}.");
    }

    [Theory]
    // The numbers that are not their own cell: a float pairs a second heap
    // cell, and a bignum or a rational indexes a table of the activation. The
    // bignum used to come back as an ArgumentOutOfRangeException from the host.
    [InlineData("10^30", "1000000000000000000000000000000")]
    [InlineData("1 rdiv 3", "1 rdiv 3")]
    [InlineData("1.5 * 2", "3.0")]
    public void AComputedNumberSurvivesToo(string expr, string expected)
    {
        var e = new PrologEngine();
        Holds(e, $"V is {expr}, ( nb_setval(k, V), fail ; true ), "
               + $"nb_getval(k, X), E is {expected}, X == E.");
    }

    [Fact]
    public void AndAcrossQueries()
    {
        var e = new PrologEngine();
        Holds(e, "nb_setval(k, f(g(h), [1,2,3])).");
        Holds(e, "numlist(1, 50000, _), garbage_collect.");
        Holds(e, "nb_getval(k, X), X == f(g(h), [1,2,3]).");
    }

    [Fact]
    public void TheAccumulatorWorksWithSomethingOtherThanAnInteger()
    {
        // The canonical fail-driven accumulator. With a compound value it used
        // to read back a term that no longer matched, and the arithmetic on
        // the next round raised instantiation_error.
        var e = new PrologEngine();
        Holds(e, "nb_setval(c, f(0)), "
               + "( between(1, 3, _), nb_getval(c, f(N)), N1 is N + 1, "
               + "  nb_setval(c, f(N1)), fail ; true ), "
               + "nb_getval(c, f(Total)), Total == 3.");
    }

    [Fact]
    public void ANonBacktrackableWriteStoresACopy()
    {
        // Reading gives a term of the current heap, fresh each time: two reads
        // do not share a variable, and neither shares with what was written.
        var e = new PrologEngine();
        Holds(e, "nb_setval(k, f(_)), nb_getval(k, f(A)), nb_getval(k, f(B)), A \\== B.");
        Holds(e, "nb_setval(k, f(X)), nb_getval(k, f(Y)), X \\== Y.");
    }

    [Fact]
    public void ACopyCarriesNoAttributes()
    {
        // The SICStus contract, and the one this engine's guide already states
        // for every copy: what carries a constraint is copy_term/3.
        var e = new PrologEngine();
        e.UseCoroutining();
        Holds(e, "dif(A, b), nb_setval(k, A), nb_getval(k, Y), term_attvars(Y, []).");
        Holds(e, "dif(A, b), nb_setval(k, A), nb_getval(k, Y), Y = b.");
        // ...and the original keeps its own.
        Holds(e, "dif(A, b), nb_setval(k, A), nb_getval(k, _), \\+ A = b.");
    }

    [Fact]
    public void TheBlackboardKeepsTheConstraintItPromises()
    {
        // bb_put/2 residualizes an attributed value through copy_term/3 and
        // re-runs the goals on every read. That contract was undone by the
        // store underneath it: after backtracking the read gave back the
        // engine's own attribute record as a term, and unifying it took the
        // process down with an exception no catch/3 could see.
        var e = new PrologEngine();
        e.UseCoroutining();
        Holds(e, "dif(A, b), ( bb_put(q, A), fail ; true ), bb_get(q, Y), \\+ Y = b.");
        Holds(e, "dif(A, b), bb_put(q, A), bb_get(q, Y), Y = c.");
        Holds(e, "( bb_put(k, f(1,2)), fail ; true ), bb_get(k, X), X == f(1,2).");
    }

    [Fact]
    public void ABacktrackableWriteSharesWithinItsQuery()
    {
        // b_setval keeps the live term: the write is undone by the trail, so
        // it cannot outlive the heap, and the sharing is what a propagation
        // queue driven through bb_b_put/2 relies on.
        var e = new PrologEngine();
        Holds(e, "b_setval(k, f(A)), b_getval(k, V), V = f(B), A == B.");
        Holds(e, "b_setval(k, 1), ( b_setval(k, 2), fail ; true ), b_getval(k, X), X == 1.");
    }

    [Fact]
    public void ABacktrackableWriteDoesNotOutliveItsQuery()
    {
        // A query is a fresh activation with a fresh heap, so an address from
        // the previous one means nothing. It reads as unset -- which is where
        // SWI leaves it too, its toplevel backtracking out of the assignment.
        var e = new PrologEngine();
        Holds(e, "b_setval(k, f(1)).");
        Assert.True(e.Query(
            "catch(b_getval(k, _), error(existence_error(variable, _), _), true).").Success);
    }

    [Fact]
    public void EnumerationSeesBothShelves()
    {
        var e = new PrologEngine();
        Holds(e, "nb_setval(one, 1), nb_setval(two, f(2)).");
        Holds(e, "findall(K-V, nb_current(K, V), L), msort(L, S), "
               + "S == [one-1, two-f(2)].");
        Holds(e, "nb_current(two, V), V == f(2).");
    }

    [Fact]
    public void TheIntegerPathIsUntouched()
    {
        // The counter this predicate exists for: still a cell, still no copy,
        // and still what it was after backtracking.
        var e = new PrologEngine();
        Holds(e, "nb_setval(c, 0), "
               + "( between(1, 1000, _), nb_getval(c, N), N1 is N + 1, "
               + "  nb_setval(c, N1), fail ; true ), "
               + "nb_getval(c, X), X == 1000.");
    }
}

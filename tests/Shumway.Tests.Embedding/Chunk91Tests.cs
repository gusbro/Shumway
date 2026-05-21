using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 91 (Phase 6): the CLP(FD) <c>all_different/1</c> global
/// constraint and reification — <c>#&lt;==&gt;</c>, <c>#==&gt;</c>,
/// <c>#&lt;==</c> and the boolean connectives <c>#/\</c>, <c>#\/</c>,
/// <c>#\</c>. A reified constraint is tied to a 0/1 variable that is 1
/// exactly when the constraint holds.
/// </summary>
public class Chunk91Tests
{
    private static Term Int(long v) => new IntTerm(v);

    private static PrologEngine Fd()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        return engine;
    }

    // ---- all_different ----

    [Fact]
    public void AllDifferent_DistinctGroundValues_Succeeds()
    {
        Assert.True(Fd().Query("all_different([1, 2, 3]).").Success);
    }

    [Fact]
    public void AllDifferent_DuplicateGroundValues_Fails()
    {
        Assert.False(Fd().Query("all_different([1, 2, 2]).").Success);
    }

    [Fact]
    public void AllDifferent_PropagatesToTheLastVariable()
    {
        var sol = Fd().Query(
            "X in 1..3, Y in 1..3, Z in 1..3, all_different([X, Y, Z]), " +
            "X = 1, Y = 2.");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["Z"]);
    }

    [Fact]
    public void AllDifferent_WithLabeling_EnumeratesEveryPermutation()
    {
        var sols = Fd().QueryAll(
            "X in 1..3, Y in 1..3, Z in 1..3, all_different([X, Y, Z]), " +
            "label([X, Y, Z]).").ToList();
        Assert.Equal(6, sols.Count);
    }

    [Fact]
    public void AllDifferent_OverconstrainedPigeonhole_HasNoSolution()
    {
        // Three variables, only two values — no all-distinct assignment.
        Assert.False(Fd().Query(
            "X in 1..2, Y in 1..2, Z in 1..2, all_different([X, Y, Z]), " +
            "label([X, Y, Z]).").Success);
    }

    [Fact]
    public void AllDistinct_BehavesAsAllDifferent()
    {
        Assert.True(Fd().Query("all_distinct([3, 1, 2]).").Success);
        Assert.False(Fd().Query("all_distinct([1, 1]).").Success);
    }

    // ---- reification: the constraint decides the boolean ----

    [Fact]
    public void Reify_EntailedConstraint_SetsBooleanToOne()
    {
        Assert.Equal(Int(1), Fd().Query("X = 1, Y = 5, B #<==> (X #< Y).")["B"]);
    }

    [Fact]
    public void Reify_DisentailedConstraint_SetsBooleanToZero()
    {
        Assert.Equal(Int(0), Fd().Query("X = 5, Y = 1, B #<==> (X #< Y).")["B"]);
    }

    [Fact]
    public void Reify_Equality_TracksWhetherValuesAreEqual()
    {
        Assert.Equal(Int(1), Fd().Query("X = 3, Y = 3, B #<==> (X #= Y).")["B"]);
        Assert.Equal(Int(0), Fd().Query("X = 3, Y = 4, B #<==> (X #= Y).")["B"]);
    }

    [Fact]
    public void Reify_Disequality_TracksWhetherValuesDiffer()
    {
        Assert.Equal(Int(1), Fd().Query("X = 3, Y = 4, B #<==> (X #\\= Y).")["B"]);
    }

    // ---- reification: the boolean decides the constraint ----

    [Fact]
    public void Reify_BooleanOne_EnforcesTheConstraint()
    {
        Assert.True(Fd().Query("B = 1, X in 1..10, B #<==> (X #< 3), X = 2.").Success);
        Assert.False(Fd().Query("B = 1, X in 1..10, B #<==> (X #< 3), X = 5.").Success);
    }

    [Fact]
    public void Reify_BooleanZero_EnforcesTheNegation()
    {
        Assert.True(Fd().Query("B = 0, X in 1..10, B #<==> (X #< 3), X = 5.").Success);
        Assert.False(Fd().Query("B = 0, X in 1..10, B #<==> (X #< 3), X = 2.").Success);
    }

    [Fact]
    public void Reify_WithLabeling_EnumeratesBothTruthValues()
    {
        var sols = Fd().QueryAll("X in 1..5, B #<==> (X #< 3), label([X, B]).")
            .Select(s => (s["X"]!, s["B"]!)).ToList();
        Assert.Equal(new[]
        {
            (Int(1), Int(1)), (Int(2), Int(1)),
            (Int(3), Int(0)), (Int(4), Int(0)), (Int(5), Int(0)),
        }, sols);
    }

    // ---- boolean connectives ----

    [Fact]
    public void Reify_Conjunction_IsOneWhenBothHold()
    {
        Assert.Equal(Int(1), Fd().Query(
            "X = 1, Y = 2, Z = 3, B #<==> ((X #< Y) #/\\ (Y #< Z)).")["B"]);
        Assert.Equal(Int(0), Fd().Query(
            "X = 1, Y = 5, Z = 3, B #<==> ((X #< Y) #/\\ (Y #< Z)).")["B"]);
    }

    [Fact]
    public void Reify_Disjunction_IsOneWhenEitherHolds()
    {
        Assert.Equal(Int(1), Fd().Query(
            "X = 5, Y = 1, Z = 10, B #<==> ((X #< Y) #\\/ (Y #< Z)).")["B"]);
        Assert.Equal(Int(0), Fd().Query(
            "X = 5, Y = 1, Z = 0, B #<==> ((X #< Y) #\\/ (Y #< Z)).")["B"]);
    }

    [Fact]
    public void Reify_Negation_InvertsTheConstraint()
    {
        Assert.Equal(Int(0), Fd().Query("X = 3, Y = 3, B #<==> (#\\ (X #= Y)).")["B"]);
        Assert.Equal(Int(1), Fd().Query("X = 3, Y = 4, B #<==> (#\\ (X #= Y)).")["B"]);
    }

    // ---- implication and top-level connectives ----

    [Fact]
    public void Implication_TrueAntecedent_ForcesConsequent()
    {
        var sol = Fd().Query("(X #= 1) #==> (Y #= 1), X = 1, Y in 1..5.");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["Y"]);
    }

    [Fact]
    public void Implication_FalseAntecedent_LeavesConsequentFree()
    {
        Assert.True(Fd().Query(
            "(X #= 1) #==> (Y #= 5), X = 2, Y in 1..3, Y = 2.").Success);
    }

    [Fact]
    public void Conjunction_TopLevel_PostsBothConstraints()
    {
        var sol = Fd().Query(
            "X in 1..5, Y in 1..5, Z in 1..5, " +
            "(X #< Y) #/\\ (Y #< Z), X = 3, Y = 4.");
        Assert.True(sol.Success);
        Assert.Equal(Int(5), sol["Z"]);
    }

    [Fact]
    public void Disjunction_TopLevel_DrivesTheRemainingDisjunct()
    {
        // One disjunct ruled out by X #\= 1 forces the other.
        var sol = Fd().Query(
            "X in 1..10, (X #= 1) #\\/ (X #= 9), X #\\= 1.");
        Assert.True(sol.Success);
        Assert.Equal(Int(9), sol["X"]);
    }

    [Fact]
    public void Negation_TopLevel_RejectsTheConstraint()
    {
        Assert.False(Fd().Query("X = 3, Y = 3, #\\ (X #= Y).").Success);
        Assert.True(Fd().Query("X = 3, Y = 4, #\\ (X #= Y).").Success);
    }

    [Fact]
    public void Reify_UnreifiableTerm_RaisesTypeError()
    {
        Assert.True(Fd().Query(
            "catch(B #<==> foobar, error(type_error(clpfd_reifiable, foobar), _), " +
            "true).").Success);
    }
}

using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 340 (Phase 28): a global linear-constraint propagator. A comparison
/// whose two sides read as one linear form sum(Ci*Vi) Rel K — with at least two
/// terms and a scaled coefficient (|Ci| &gt;= 2, which every crypt-arithmetic
/// column has) — posts a single bounds-consistency propagator over the whole
/// sum instead of decomposing it into a tree of binary $fd_plus / $fd_times.
/// The global form combines a variable's coefficients (so a variable repeated
/// across columns is handled exactly) and prunes each variable against every
/// other at once, which is far stronger than the binary decomposition and lets
/// leftmost labeling solve puzzles like donald that previously did not
/// terminate.
/// </summary>
public class Chunk340Tests
{
    private static PrologEngine Fd()
    {
        var e = new PrologEngine();
        e.UseClpfd();
        return e;
    }

    private static Term Int(long v) => new IntTerm(v);

    // Repeated variable: 2*X + X is the combined 3*X; without coefficient
    // combining the binary form treats the two X occurrences independently and
    // mis-narrows.
    [Fact]
    public void RepeatedVariable_CombinesCoefficients()
    {
        var sol = Fd().Query("X in 0..9, 2*X + X #= 9.");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["X"]);
    }

    // The X = Y value-coincidence case the prefix/suffix rest-sum must handle:
    // X + Y #= 6 over 1..5 must keep the 3-3 solution.
    [Fact]
    public void EqualValueSolution_NotDropped()
    {
        var sols = Fd().QueryAll(
            "X in 1..5, Y in 1..5, X + Y #= 6, label([X, Y]).").ToList();
        Assert.Equal(5, sols.Count);
        Assert.Contains(sols, s => s["X"].Equals(Int(3)) && s["Y"].Equals(Int(3)));
    }

    // Scaled coefficients pin a unique solution by propagation alone.
    [Fact]
    public void ScaledSum_PinsDigits()
    {
        var sol = Fd().Query("X in 0..9, Y in 0..9, 10*X + Y #= 34, label([X, Y]).");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["X"]);
        Assert.Equal(Int(4), sol["Y"]);
    }

    // A small two-equation linear system, solved by propagation + labeling.
    [Fact]
    public void TwoEquationSystem_Solves()
    {
        var sol = Fd().Query(
            "X in 0..9, Y in 0..9, X + Y #= 10, X + 2*Y #= 14, label([X, Y]).");
        Assert.True(sol.Success);
        Assert.Equal(Int(6), sol["X"]);
        Assert.Equal(Int(4), sol["Y"]);
    }

    // Negative coefficients enumerate the right solution set.
    [Fact]
    public void NegativeCoefficients_EnumerateCorrectly()
    {
        var sols = Fd().QueryAll(
            "X in 0..9, Y in 0..9, 3*X - 2*Y #= 1, label([X, Y]).").ToList();
        var pairs = sols.Select(s => (((IntTerm)s["X"]).Value, ((IntTerm)s["Y"]).Value))
                        .OrderBy(p => p.Item1).ToList();
        Assert.Equal(new[] { (1L, 1L), (3L, 4L), (5L, 7L) }, pairs);
    }

    // The headline case: donald solved by plain LEFTMOST labeling. Before the
    // global propagator this did not terminate; the strong combined-coefficient
    // propagation now makes leftmost feasible. (ff is the fast order; this test
    // uses leftmost precisely because it exercises the propagation strength.)
    [Fact]
    public void Donald_SolvesUnderLeftmostLabeling()
    {
        // donald's solution is unique, so leftmost label binds LD to it; the
        // trailing == confirms the exact assignment without unpacking the list.
        var sol = Fd().Query(
            "LD = [D,O,N,A,L,G,E,R,B,T], LD ins 0..9, all_different(LD), "
            + "D in 1..9, G in 1..9, "
            + "100000*D + 10000*O + 1000*N + 100*A + 10*L + D "
            + "+ 100000*G + 10000*E + 1000*R + 100*A + 10*L + D "
            + "#= 100000*R + 10000*O + 1000*B + 100*E + 10*R + T, "
            + "label(LD), LD == [5,2,6,4,8,1,9,7,3,0].");
        Assert.True(sol.Success);
    }

    // The threshold leaves unit-coefficient sums on the existing decomposition:
    // plain A + B #= C still propagates forward.
    [Fact]
    public void UnitCoefficientSum_StillPropagates()
    {
        var sol = Fd().Query("A in 2..2, B in 3..3, A + B #= C.");
        Assert.True(sol.Success);
        Assert.Equal(Int(5), sol["C"]);
    }
}

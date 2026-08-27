using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The simplex on its own, before any Prolog reaches it. Rows read
/// <c>a·x + c &gt;= 0</c>, which is the shape the CLP(R) store keeps its
/// inequalities in.
/// </summary>
public class LinearProgramTests
{
    private static (LpStatus Status, double Value, double[] Vertex) Run(
        double[][] rows, double[] objective, bool maximise)
    {
        var status = LinearProgram.Solve(rows, objective, maximise, out double v, out double[] x);
        return (status, v, x);
    }

    [Fact]
    public void BoundsOnOneVariable()
    {
        // 3 =< x =< 9, as (x - 3 >= 0) and (9 - x >= 0).
        var rows = new[] { new[] { 1.0, -3.0 }, new[] { -1.0, 9.0 } };
        var lo = Run(rows, new[] { 1.0, 0.0 }, maximise: false);
        Assert.Equal(LpStatus.Optimal, lo.Status);
        Assert.Equal(3.0, lo.Value, 9);
        Assert.Equal(3.0, lo.Vertex[0], 9);

        var hi = Run(rows, new[] { 1.0, 0.0 }, maximise: true);
        Assert.Equal(LpStatus.Optimal, hi.Status);
        Assert.Equal(9.0, hi.Value, 9);
        Assert.Equal(9.0, hi.Vertex[0], 9);
    }

    [Fact]
    public void VariablesAreFree()
    {
        // x =< 4 with nothing below: minimising is unbounded, maximising is 4.
        // A solver that assumed x >= 0 would answer 0 for the minimum.
        var rows = new[] { new[] { -1.0, 4.0 } };
        Assert.Equal(LpStatus.Unbounded, Run(rows, new[] { 1.0, 0.0 }, false).Status);

        var hi = Run(rows, new[] { 1.0, 0.0 }, true);
        Assert.Equal(LpStatus.Optimal, hi.Status);
        Assert.Equal(4.0, hi.Value, 9);

        // And a genuinely negative optimum comes out negative.
        var lo = Run(new[] { new[] { 1.0, 7.0 } }, new[] { 1.0, 0.0 }, false);
        Assert.Equal(LpStatus.Optimal, lo.Status);
        Assert.Equal(-7.0, lo.Value, 9);
    }

    [Fact]
    public void TheObjectiveCanBeAnExpression()
    {
        // 2 =< x =< 4, objective 2x + 1.
        var rows = new[] { new[] { 1.0, -2.0 }, new[] { -1.0, 4.0 } };
        Assert.Equal(5.0, Run(rows, new[] { 2.0, 1.0 }, false).Value, 9);
        Assert.Equal(9.0, Run(rows, new[] { 2.0, 1.0 }, true).Value, 9);
    }

    [Fact]
    public void TwoVariablesAndAVertex()
    {
        // x >= 1, y >= 1, x + y =< 10: minimising x + 2y sits at (1, 1).
        var rows = new[]
        {
            new[] { 1.0, 0.0, -1.0 },
            new[] { 0.0, 1.0, -1.0 },
            new[] { -1.0, -1.0, 10.0 },
        };
        var r = Run(rows, new[] { 1.0, 2.0, 0.0 }, false);
        Assert.Equal(LpStatus.Optimal, r.Status);
        Assert.Equal(3.0, r.Value, 9);
        Assert.Equal(1.0, r.Vertex[0], 9);
        Assert.Equal(1.0, r.Vertex[1], 9);

        // Maximising the same objective pushes y up to 9.
        var hi = Run(rows, new[] { 1.0, 2.0, 0.0 }, true);
        Assert.Equal(19.0, hi.Value, 9);
        Assert.Equal(1.0, hi.Vertex[0], 9);
        Assert.Equal(9.0, hi.Vertex[1], 9);
    }

    [Fact]
    public void TheVertexSatisfiesEveryRow()
    {
        // The point matters as much as the value: branch and bound reads it.
        var rows = new[]
        {
            new[] { 1.0, 1.0, -4.0 },     // x + y >= 4
            new[] { -1.0, 2.0, 2.0 },     // 2y - x + 2 >= 0
            new[] { 1.0, -1.0, 6.0 },     // x - y + 6 >= 0
        };
        var r = Run(rows, new[] { 3.0, 1.0, 0.0 }, false);
        Assert.Equal(LpStatus.Optimal, r.Status);
        foreach (var row in rows)
        {
            double lhs = row[0] * r.Vertex[0] + row[1] * r.Vertex[1] + row[2];
            Assert.True(lhs > -1e-7, $"vertex violates a row by {lhs}");
        }
        Assert.Equal(3.0 * r.Vertex[0] + r.Vertex[1], r.Value, 7);
    }

    [Fact]
    public void ContradictionIsInfeasible()
    {
        // x >= 5 and x =< 3 together.
        var rows = new[] { new[] { 1.0, -5.0 }, new[] { -1.0, 3.0 } };
        Assert.Equal(LpStatus.Infeasible, Run(rows, new[] { 1.0, 0.0 }, false).Status);
    }

    [Fact]
    public void NoConstraintsAtAll()
    {
        // Nothing to satisfy: any objective mentioning a variable is unbounded,
        // a constant one is its own optimum.
        Assert.Equal(LpStatus.Unbounded,
            Run(System.Array.Empty<double[]>(), new[] { 1.0, 0.0 }, false).Status);
        var c = Run(System.Array.Empty<double[]>(), new[] { 0.0, 42.0 }, false);
        Assert.Equal(LpStatus.Optimal, c.Status);
        Assert.Equal(42.0, c.Value, 9);
    }

    [Fact]
    public void ADegenerateProgramTerminates()
    {
        // Redundant and duplicated rows meeting at one point: the shape that
        // cycles under a naive pivot rule. Bland's is why this returns.
        var rows = new[]
        {
            new[] { 1.0, 0.0, 0.0 },
            new[] { 1.0, 0.0, 0.0 },
            new[] { 0.0, 1.0, 0.0 },
            new[] { 1.0, 1.0, 0.0 },
            new[] { -1.0, -1.0, 6.0 },
        };
        var r = Run(rows, new[] { 1.0, 1.0, 0.0 }, false);
        Assert.Equal(LpStatus.Optimal, r.Status);
        Assert.Equal(0.0, r.Value, 9);
    }
}

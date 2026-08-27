namespace Shumway.Embedding;

/// <summary>What optimising a linear program produced.</summary>
internal enum LpStatus
{
    /// <summary>A finite optimum, and a point that attains it.</summary>
    Optimal,

    /// <summary>Feasible, but the objective decreases without bound.</summary>
    Unbounded,

    /// <summary>No point satisfies the constraints.</summary>
    Infeasible,
}

/// <summary>
/// Two-phase simplex over the CLP(R) store's inequalities.
///
/// <para>It answers what Fourier-Motzkin cannot: not just the bound on an
/// objective but a POINT that attains it. That point is what branch and bound
/// needs — it is how you learn which integer variable came out fractional and
/// where to split — so <c>bb_inf/3</c> rests on this and <c>inf/2</c> stops
/// paying for an elimination whose intermediate systems can square in size per
/// variable removed.</para>
///
/// <para>The store hands over only inequalities: equalities are already in
/// solved form (each dependent variable IS a linear form of the others), so
/// expanding an inequality substitutes them away. Every variable is FREE, as a
/// real is, so each is split into a difference of two non-negative columns
/// rather than assumed non-negative.</para>
///
/// <para>Strict inequalities are optimised as non-strict. The infimum of
/// <c>X &gt; 3</c> is 3 whether or not it is attained, which is what SWI and
/// SICStus report; the store keeps the strictness for satisfiability.</para>
///
/// <para>Bland's rule throughout: it is the slow pivot choice, and it is the
/// one that cannot cycle. A constraint solver that hangs on a degenerate
/// program is worse than one that takes more pivots.</para>
/// </summary>
internal static class LinearProgram
{
    private const double Eps = 1e-9;

    /// <summary>Optimises <paramref name="objective"/> subject to
    /// <paramref name="rows"/>.
    ///
    /// <para>Each row is <c>[a0 … a(n-1), c]</c> and reads
    /// <c>a·x + c &gt;= 0</c>. The objective has the same shape and its
    /// constant is carried through to the reported value.</para></summary>
    internal static LpStatus Solve(
        double[][] rows, double[] objective, bool maximise,
        out double value, out double[] vertex)
    {
        int n = objective.Length - 1;          // free variables
        int m = rows.Length;
        value = 0;
        vertex = new double[n];

        // Columns: p_0..p_{n-1}, q_0..q_{n-1} (x_j = p_j - q_j), one surplus
        // per row, one artificial per row.
        int pq = 2 * n;
        int surplus = pq;
        int artificial = surplus + m;
        int cols = artificial + m + 1;         // + RHS
        int rhs = cols - 1;

        var t = new double[m + 2][];           // rows, then two cost rows
        for (int i = 0; i < t.Length; i++) t[i] = new double[cols];

        for (int i = 0; i < m; i++)
        {
            // a·x + c >= 0  →  a·p - a·q - s = -c
            double c = rows[i][n];
            double sign = -c < 0 ? -1.0 : 1.0;   // keep the RHS non-negative
            for (int j = 0; j < n; j++)
            {
                double a = rows[i][j] * sign;
                t[i][j] = a;
                t[i][n + j] = -a;
            }
            t[i][surplus + i] = -sign;
            t[i][artificial + i] = 1;
            t[i][rhs] = -c * sign;
        }

        // Phase II cost (row m): minimise the objective, so maximising is the
        // same problem with the signs flipped.
        double dir = maximise ? -1.0 : 1.0;
        for (int j = 0; j < n; j++)
        {
            t[m][j] = objective[j] * dir;
            t[m][n + j] = -objective[j] * dir;
        }
        // Phase I cost (row m+1): minimise the sum of the artificials.
        for (int i = 0; i < m; i++) t[m + 1][artificial + i] = 1;

        var basis = new int[m];
        for (int i = 0; i < m; i++) basis[i] = artificial + i;

        // Price out the artificial basis so the Phase I row reads the true
        // reduced costs.
        for (int i = 0; i < m; i++) AddMultiple(t[m + 1], t[i], -1, cols);

        if (m > 0)
        {
            Pivot(t, basis, m + 1, m, cols, rhs);
            // The cost row carries the NEGATED objective in its RHS cell, so
            // artificials still summing to something is -rhs > 0.
            if (-t[m + 1][rhs] > Eps) return LpStatus.Infeasible;
            DriveArtificialsOut(t, basis, m, cols, rhs, artificial);
        }

        // Phase II, on the same tableau with the artificials pinned out.
        for (int i = 0; i < m; i++)
            if (System.Math.Abs(t[m][basis[i]]) > Eps)
                AddMultiple(t[m], t[i], -t[m][basis[i]], cols);

        if (!Pivot(t, basis, m, m, cols, rhs, artificial))
            return LpStatus.Unbounded;

        for (int i = 0; i < m; i++)
        {
            int b = basis[i];
            if (b < n) vertex[b] += t[i][rhs];
            else if (b < pq) vertex[b - n] -= t[i][rhs];
        }
        // The cost row holds -(objective) at the optimum.
        value = -t[m][rhs] * dir + objective[n];
        return LpStatus.Optimal;
    }

    /// <summary>Simplex iterations on <paramref name="costRow"/>. Returns false
    /// when a column can improve the cost with no row to limit it: unbounded.
    /// Columns at or above <paramref name="forbidden"/> are never entered,
    /// which is how Phase II keeps the artificials out.</summary>
    private static bool Pivot(
        double[][] t, int[] basis, int costRow, int m, int cols, int rhs,
        int forbidden = int.MaxValue)
    {
        while (true)
        {
            // Bland: the LOWEST-indexed improving column.
            int enter = -1;
            for (int j = 0; j < cols - 1 && j < forbidden; j++)
                if (t[costRow][j] < -Eps) { enter = j; break; }
            if (enter < 0) return true;

            int leave = -1;
            double best = 0;
            for (int i = 0; i < m; i++)
            {
                if (t[i][enter] <= Eps) continue;
                double ratio = t[i][rhs] / t[i][enter];
                // Bland again on ties: the smallest basis index leaves.
                if (leave < 0 || ratio < best - Eps
                    || (ratio < best + Eps && basis[i] < basis[leave]))
                {
                    leave = i;
                    best = ratio;
                }
            }
            if (leave < 0) return false;

            Eliminate(t, basis, leave, enter, m, cols, costRow);
        }
    }

    /// <summary>Replaces any artificial still in the basis at value zero, so
    /// Phase II starts from a basis of real columns. A row that cannot be
    /// pivoted is redundant and drops out of consideration.</summary>
    private static void DriveArtificialsOut(
        double[][] t, int[] basis, int m, int cols, int rhs, int artificial)
    {
        for (int i = 0; i < m; i++)
        {
            if (basis[i] < artificial) continue;
            for (int j = 0; j < artificial; j++)
            {
                if (System.Math.Abs(t[i][j]) <= Eps) continue;
                Eliminate(t, basis, i, j, m, cols, -1);
                break;
            }
        }
    }

    private static void Eliminate(
        double[][] t, int[] basis, int leave, int enter, int m, int cols,
        int costRow)
    {
        double p = t[leave][enter];
        for (int j = 0; j < cols; j++) t[leave][j] /= p;
        for (int i = 0; i < t.Length; i++)
        {
            if (i == leave) continue;
            // The Phase I row must keep being updated during Phase II or its
            // artificials could creep back in; updating both costs is cheaper
            // than reasoning about when it matters.
            if (costRow >= 0 && i > m && i != costRow && i != m + 1) continue;
            double f = t[i][enter];
            if (System.Math.Abs(f) > Eps) AddMultiple(t[i], t[leave], -f, cols);
        }
        basis[leave] = enter;
    }

    private static void AddMultiple(double[] target, double[] source, double f, int cols)
    {
        for (int j = 0; j < cols; j++) target[j] += f * source[j];
    }
}

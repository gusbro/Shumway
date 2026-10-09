namespace Shumway.Builtins;

/// <summary>
/// cumulative's pruning by its time table, over the domains of the tasks'
/// starts and resource uses, shrunk in place.
///
/// <para>A task surely runs from its latest start to its earliest end; that
/// stretch, at the task's least use, is its compulsory part, and the parts
/// laid side by side give the profile of what the resource surely carries.
/// <see cref="Prune"/> fails when the profile passes the limit anywhere. A
/// task does not fit over a stretch where the others already leave less than
/// its least use: the starts that would run it over that stretch go. While a
/// task surely runs, it uses at most what the others leave. The pass repeats
/// until nothing changes. Once every start, duration and use is known the
/// compulsory parts are the tasks themselves, and the check is exact.</para>
///
/// <para>The profile misses work that has room to move: twelve tasks of
/// total work 61 fit under a limit of 4 in no horizon shorter than 16, which
/// no compulsory part shows. So it then weighs windows of time: the work the
/// tasks cannot help doing inside one must fit in what the limit leaves
/// there.</para>
///
/// <para>A task of no duration occupies no time, whatever it uses.</para>
/// </summary>
internal static class ClpfdCumulative
{
    // Ends before starts at the same time: a task that ends where another
    // starts never shares a moment with it.
    private static readonly Comparer<(long Time, long Delta)> ByTime =
        Comparer<(long Time, long Delta)>.Create(static (a, b) =>
            a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Delta.CompareTo(b.Delta));

    /// <summary>False when the tasks cannot fit under the limit.
    /// <paramref name="durations"/> are the tasks' least durations; a limit of
    /// <see cref="ClpfdDomain.Sup"/> bounds nothing.</summary>
    internal static bool Prune(ClpfdDomain[] starts, long[] durations, ClpfdDomain[] uses, long limit)
    {
        int n = starts.Length;
        for (int i = 0; i < n; i++)
            if (starts[i].IsEmpty || uses[i].IsEmpty) return false;
        if (limit == ClpfdDomain.Sup) return true;
        // The compulsory part each task had when the profile was laid out:
        // [partLo, partHi) at partUse, none when partUse is zero.
        var partLo = new long[n];
        var partHi = new long[n];
        var partUse = new long[n];
        var events = new (long Time, long Delta)[2 * n];
        var segLo = new long[2 * n];
        var segHi = new long[2 * n];
        var segUse = new long[2 * n];
        bool changed = true;
        while (changed)
        {
            changed = false;
            int ne = 0;
            for (int i = 0; i < n; i++)
            {
                partLo[i] = starts[i].Max;
                partHi[i] = starts[i].Min + durations[i];
                partUse[i] = durations[i] > 0 && partLo[i] < partHi[i] ? uses[i].Min : 0;
                if (partUse[i] > 0)
                {
                    events[ne++] = (partLo[i], partUse[i]);
                    events[ne++] = (partHi[i], -partUse[i]);
                }
            }
            Array.Sort(events, 0, ne, ByTime);
            // Each addition keeps the height within two inline integers: the
            // check comes before the next one.
            int ns = 0;
            long height = 0;
            for (int e = 0; e < ne;)
            {
                long t = events[e].Time;
                while (e < ne && events[e].Time == t)
                {
                    height += events[e++].Delta;
                    if (height > limit) return false;
                }
                if (height > 0 && e < ne)
                {
                    segLo[ns] = t;
                    segHi[ns] = events[e].Time;
                    segUse[ns++] = height;
                }
            }

            for (int i = 0; i < n; i++)
            {
                long d = durations[i];
                if (d <= 0) continue;
                long least = uses[i].Min;
                bool hasPart = partUse[i] > 0 || partLo[i] < partHi[i];
                // A task that runs at all uses at most the limit.
                long most = limit;
                ClpfdDomain s = starts[i];
                for (int k = 0; k < ns; k++)
                {
                    bool inPart = hasPart && segLo[k] < partHi[i] && segHi[k] > partLo[i];
                    long others = segUse[k] - (inPart ? partUse[i] : 0);
                    if (inPart && limit - others < most) most = limit - others;
                    // The starts in (segLo - d, segHi) run the task over the stretch.
                    if (least > 0 && others + least > limit)
                        s = s.RemoveInterval(segLo[k] - d + 1, segHi[k] - 1);
                }
                if (!s.SameAs(starts[i]))
                {
                    if (s.IsEmpty) return false;
                    starts[i] = s;
                    changed = true;
                }
                if (uses[i].Max > most)
                {
                    var u = uses[i].Above(most);
                    if (u.IsEmpty) return false;
                    uses[i] = u;
                    changed = true;
                }
            }
        }
        return WorkFits(starts, durations, uses, limit);
    }

    // Past this many tasks the windows, cubic in the tasks, cost more than
    // they find.
    private const int MaxWorkTasks = 100;

    /// <summary>False when some window, from a task's earliest start to a
    /// task's latest end, must hold more work than the limit leaves in it. A
    /// task's work inside the window is the least it can do there: its least
    /// duration at its least use, cut by the window with the task as far left
    /// or as far right as its start allows.</summary>
    private static bool WorkFits(ClpfdDomain[] starts, long[] durations, ClpfdDomain[] uses, long limit)
    {
        int n = starts.Length;
        if (n > MaxWorkTasks || limit < 0) return true;
        try
        {
            checked
            {
                for (int i = 0; i < n; i++)
                {
                    long a = starts[i].Min;
                    for (int j = 0; j < n; j++)
                    {
                        long b = starts[j].Max + durations[j];
                        if (b <= a) continue;
                        long work = 0;
                        for (int k = 0; k < n; k++)
                        {
                            long d = durations[k], c = uses[k].Min;
                            if (d <= 0 || c <= 0) continue;
                            long inside = Math.Min(Math.Min(d, b - a),
                                Math.Min(starts[k].Min + d - a, b - starts[k].Max));
                            if (inside > 0) work += inside * c;
                        }
                        if (work > limit * (b - a)) return false;
                    }
                }
            }
        }
        // A window too wide to weigh in a long gives no verdict.
        catch (OverflowException) { }
        return true;
    }
}

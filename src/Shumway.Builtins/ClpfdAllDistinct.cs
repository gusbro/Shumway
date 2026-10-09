namespace Shumway.Builtins;

/// <summary>
/// all_distinct's pruning over an array of domains, shrunk in place.
///
/// <para><see cref="Prune"/> keeps exactly the values that some assignment of
/// distinct values to all the variables gives (domain consistency): a maximum
/// matching of variables to values, then an edge outside the matching
/// survives only when it lies on an alternating cycle (its two ends share a
/// strongly connected component) or on an alternating path from a value
/// the matching leaves free. Domains too wide to list fall back to
/// <see cref="Hall"/>, which reasons on intervals only.</para>
///
/// <para>Every walk keeps its work on explicit stacks and queues: the
/// variable count is the program's, and a frame per step would make it the
/// C# stack's.</para>
/// </summary>
internal static class ClpfdAllDistinct
{
    // Past this many variable-value pairs the graph costs more than the
    // interval reasoning it would improve on.
    private const long MaxEdges = 100_000;
    private const long Infinite = long.MaxValue;

    /// <summary>False when no assignment of distinct values exists.</summary>
    internal static bool Prune(ClpfdDomain[] doms)
    {
        int n = doms.Length;
        long edges = 0, lo = long.MaxValue, hi = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (doms[i].IsEmpty) return false;
            long size = doms[i].Size(Infinite);
            if (size == Infinite || (edges += size) > MaxEdges) return Hall(doms);
            if (doms[i].Min < lo) lo = doms[i].Min;
            if (doms[i].Max > hi) hi = doms[i].Max;
        }
        if (n < 2) return true;
        if (hi - lo >= MaxEdges) return Hall(doms);

        // The values present, ascending, numbered 0..m-1; each variable's
        // values as those numbers.
        int range = (int)(hi - lo + 1);
        var number = new int[range];
        foreach (var d in doms)
            foreach (long v in d.Values()) number[v - lo] = 1;
        int m = 0;
        for (int r = 0; r < range; r++)
            number[r] = number[r] != 0 ? m++ : -1;
        if (m < n) return false;
        var values = new long[m];
        for (int r = 0; r < range; r++)
            if (number[r] >= 0) values[number[r]] = lo + r;

        var adjStart = new int[n + 1];
        var adj = new int[edges];
        for (int i = 0, e = 0; i < n; i++)
        {
            adjStart[i] = e;
            foreach (long v in doms[i].Values()) adj[e++] = number[v - lo];
            adjStart[i + 1] = e;
        }

        var matchVar = new int[n];
        var matchVal = new int[m];
        System.Array.Fill(matchVar, -1);
        System.Array.Fill(matchVal, -1);
        if (!Match(n, m, adjStart, adj, matchVar, matchVal)) return false;

        // The residual graph: variable i -> its matched value (node n + j),
        // value j -> every variable that could take it but does not.
        var outCount = new int[m + 1];
        for (int i = 0; i < n; i++)
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++)
                if (adj[e] != matchVar[i]) outCount[adj[e] + 1]++;
        for (int j = 0; j < m; j++) outCount[j + 1] += outCount[j];
        var valOutStart = outCount;
        var valOut = new int[valOutStart[m]];
        var fill = new int[m];
        for (int i = 0; i < n; i++)
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++)
            {
                int j = adj[e];
                if (j != matchVar[i]) valOut[valOutStart[j] + fill[j]++] = i;
            }

        // Values on an alternating path from a free value.
        var reached = new bool[m];
        var queue = new int[m];
        int head = 0, tail = 0;
        for (int j = 0; j < m; j++)
            if (matchVal[j] < 0) { reached[j] = true; queue[tail++] = j; }
        while (head < tail)
        {
            int j = queue[head++];
            for (int k = valOutStart[j]; k < valOutStart[j + 1]; k++)
            {
                int next = matchVar[valOut[k]];
                if (!reached[next]) { reached[next] = true; queue[tail++] = next; }
            }
        }

        int[] component = Components(n, m, matchVar, valOutStart, valOut);

        var keep = new System.Collections.Generic.List<long>();
        for (int i = 0; i < n; i++)
        {
            int kept = 0;
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++)
            {
                int j = adj[e];
                if (j == matchVar[i] || reached[j] || component[i] == component[n + j]) kept++;
            }
            if (kept == adjStart[i + 1] - adjStart[i]) continue;
            keep.Clear();
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++)
            {
                int j = adj[e];
                if (j == matchVar[i] || reached[j] || component[i] == component[n + j])
                    keep.Add(values[j]);
            }
            doms[i] = FromAscending(keep);
        }
        return true;
    }

    /// <summary>A maximum matching, by an augmenting path from each variable
    /// in turn (breadth first). False when some variable is left without a
    /// value.</summary>
    private static bool Match(int n, int m, int[] adjStart, int[] adj, int[] matchVar, int[] matchVal)
    {
        // Greedy first: most variables find a free value at once.
        for (int i = 0; i < n; i++)
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++)
                if (matchVal[adj[e]] < 0) { matchVar[i] = adj[e]; matchVal[adj[e]] = i; break; }

        var parent = new int[m];          // the variable a value was reached from
        var seen = new int[m];            // the search that reached it, + 1
        var queue = new int[n];
        for (int root = 0; root < n; root++)
        {
            if (matchVar[root] >= 0) continue;
            int stamp = root + 1, head = 0, tail = 0, free = -1;
            queue[tail++] = root;
            while (head < tail && free < 0)
            {
                int x = queue[head++];
                for (int e = adjStart[x]; e < adjStart[x + 1]; e++)
                {
                    int j = adj[e];
                    if (seen[j] == stamp) continue;
                    seen[j] = stamp;
                    parent[j] = x;
                    if (matchVal[j] < 0) { free = j; break; }
                    queue[tail++] = matchVal[j];
                }
            }
            if (free < 0) return false;
            for (int j = free; ;)
            {
                int x = parent[j], previous = matchVar[x];
                matchVar[x] = j;
                matchVal[j] = x;
                if (x == root) break;
                j = previous;
            }
        }
        return true;
    }

    /// <summary>Strongly connected components of the residual graph (nodes
    /// 0..n-1 the variables, n..n+m-1 the values), Tarjan's algorithm with
    /// its recursion on explicit stacks.</summary>
    private static int[] Components(int n, int m, int[] matchVar, int[] valOutStart, int[] valOut)
    {
        int size = n + m;
        var index = new int[size];
        var low = new int[size];
        var component = new int[size];
        var onStack = new bool[size];
        System.Array.Fill(index, -1);
        var stack = new int[size];
        var callNode = new int[size];
        var callNext = new int[size];
        int sp = 0, depth = 0, counter = 0, components = 0;

        for (int start = 0; start < size; start++)
        {
            if (index[start] >= 0) continue;
            index[start] = low[start] = counter++;
            stack[sp++] = start; onStack[start] = true;
            callNode[0] = start; callNext[0] = 0; depth = 1;
            while (depth > 0)
            {
                int v = callNode[depth - 1];
                int w = -1;
                if (v < n)
                {
                    if (callNext[depth - 1]++ == 0) w = n + matchVar[v];
                }
                else
                {
                    int j = v - n, k = valOutStart[j] + callNext[depth - 1];
                    if (k < valOutStart[j + 1]) { w = valOut[k]; callNext[depth - 1]++; }
                }
                if (w >= 0)
                {
                    if (index[w] < 0)
                    {
                        index[w] = low[w] = counter++;
                        stack[sp++] = w; onStack[w] = true;
                        callNode[depth] = w; callNext[depth] = 0; depth++;
                    }
                    else if (onStack[w] && index[w] < low[v]) low[v] = index[w];
                    continue;
                }
                if (low[v] == index[v])
                {
                    int x;
                    do { x = stack[--sp]; onStack[x] = false; component[x] = components; }
                    while (x != v);
                    components++;
                }
                depth--;
                if (depth > 0)
                {
                    int u = callNode[depth - 1];
                    if (low[v] < low[u]) low[u] = low[v];
                }
            }
        }
        return component;
    }

    private static ClpfdDomain FromAscending(System.Collections.Generic.List<long> values)
    {
        var bounds = new System.Collections.Generic.List<long>();
        for (int k = 0; k < values.Count; k++)
        {
            if (k == 0 || values[k] != values[k - 1] + 1)
            {
                if (k > 0) bounds.Add(values[k - 1]);
                bounds.Add(values[k]);
            }
        }
        if (values.Count > 0) bounds.Add(values[^1]);
        return ClpfdDomain.FromBounds(bounds.ToArray());
    }

    /// <summary>Hall intervals: an interval holding as many domains as it has
    /// values is theirs, and leaves every other domain; more domains than
    /// values is a failure. Bounds reasoning, for domains of any width.</summary>
    internal static bool Hall(ClpfdDomain[] work)
    {
        int n = work.Length;
        for (int li = 0; li < n; li++)
        {
            if (work[li].IsEmpty) return false;
            long lo = work[li].Min;
            if (lo == ClpfdDomain.Inf) continue;
            for (int hj = 0; hj < n; hj++)
            {
                long hi = work[hj].Max;
                if (hi == ClpfdDomain.Sup || lo > hi) continue;
                int count = 0;
                for (int k = 0; k < n; k++)
                    if (work[k].Within(lo, hi)) count++;
                long size = hi - lo + 1;
                if (count > size) return false;
                if (count == size)
                    for (int k = 0; k < n; k++)
                        if (!work[k].Within(lo, hi))
                            work[k] = work[k].RemoveInterval(lo, hi);
            }
        }
        return true;
    }
}

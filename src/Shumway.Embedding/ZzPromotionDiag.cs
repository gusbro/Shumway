using System.Collections;
using System.Reflection;
using System.Text;
using Shumway.Core;

namespace Shumway.Embedding;

// DIAG (temporary, not for merge): when a wait for promotions times out, what
// the compile workers run and what waits in their queue, grouped by the engine
// that queued it, appended to TestResults/parallel/zzdiag-<pid>.log (the CI
// uploads that folder when a lane fails); then whether the waiting engine's
// compiles were delivered 30, 60 and 120 s later.
internal static class ZzPromotionDiag
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly object Gate = new();

    private static string Name(int fid)
    {
        try { var (a, ar) = FunctorTable.Lookup(fid); return $"{AtomTable.GetById(a)?.Name}/{ar}"; }
        catch { return fid.ToString(); }
    }

    // The store a work closure captured, and the predicate it compiles.
    private static void Captured(object? obj, int depth, ref IlPromotionStore? store, ref string? pred)
    {
        if (obj is null || depth > 3) return;
        foreach (var f in obj.GetType().GetFields(Any))
        {
            object? v;
            try { v = f.GetValue(obj); } catch { continue; }
            if (v is null) continue;
            if (store is null && v is IlPromotionStore s) store = s;
            else if (pred is null && v is int fid && f.Name.Contains("functorId", StringComparison.OrdinalIgnoreCase)) pred = Name(fid);
            else if (pred is null && v is Shumway.Compiler.Wam.CompiledPredicate p) pred = Name(p.FunctorId);
            else if (v is Delegate d) Captured(d.Target, depth + 1, ref store, ref pred);
            else if (v.GetType().Name.Contains("DisplayClass") || v.GetType().Name.StartsWith("<>c", StringComparison.Ordinal))
                Captured(v, depth + 1, ref store, ref pred);
        }
    }

    private static string Describe(Delegate work, IlPromotionStore ours)
    {
        IlPromotionStore? store = null; string? pred = null;
        Captured(work.Target, 0, ref store, ref pred);
        string who = store is null ? "no-store" : ReferenceEquals(store, ours) ? "OURS" : $"store#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(store):x}";
        return $"{who} {work.Method.Name} {pred ?? "?"}";
    }

    private static string StoreState(IlPromotionStore st)
    {
        string F(string name)
        {
            var f = typeof(IlPromotionStore).GetField(name, Any);
            object? v = f?.GetValue(st);
            return v switch
            {
                IDictionary d => $"{name}.keys=[{string.Join(",", d.Keys.Cast<object>().Take(12))}]",
                IEnumerable e when name is "_pendingCompiles" or "_pendingCps"
                    => $"{name}=[{string.Join(",", e.Cast<object>().Take(12).Select(x => x is int fid ? Name(fid) : x?.ToString()))}]",
                ICollection c => $"{name}.Count={c.Count}",
                _ => $"{name}={v}",
            };
        }
        return string.Join(" ", new[] { "_emitTicketNext", "_emitTurnNow", "_deliverNext", "_undelivered",
            "_pendingCompiles", "_pendingCps", "_completedCompiles" }.Select(F));
    }

    private static string Snapshot(IlPromotionStore ours)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  workers: " + IlCompileWorker.Describe());
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (thread, (work, started)) in IlCompileWorker.ZzRunning)
            sb.AppendLine($"  running on {thread} for {(now - started) * 1000 / System.Diagnostics.Stopwatch.Frequency} ms: {Describe(work, ours)}");
        var queued = IlCompileWorker.ZzQueued();
        var groups = new Dictionary<string, (int Count, int First, HashSet<string> Preds)>();
        for (int i = 0; i < queued.Length; i++)
        {
            string d = Describe(queued[i], ours);
            string key = d.Substring(0, d.IndexOf(' '));
            if (!groups.TryGetValue(key, out var g)) g = (0, i, new HashSet<string>());
            g.Count++;
            if (g.Preds.Count < 10) g.Preds.Add(d.Substring(d.IndexOf(' ') + 1));
            groups[key] = g;
        }
        sb.AppendLine($"  queue: {queued.Length} items in {groups.Count} groups");
        foreach (var (key, g) in groups.OrderBy(kv => kv.Value.First).Take(12))
            sb.AppendLine($"    {key}: {g.Count} items, first at {g.First}: {string.Join(" | ", g.Preds)}");
        sb.AppendLine("  ours: " + StoreState(ours));
        return sb.ToString();
    }

    private static string LogPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Shumway.slnx")))
                return Path.Combine(dir.FullName, "TestResults", "parallel", $"zzdiag-{Environment.ProcessId}.log");
        return Path.Combine(Path.GetTempPath(), $"zzdiag-{Environment.ProcessId}.log");
    }

    private static void Append(string text)
    {
        try
        {
            string path = LogPath();
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, text);
            }
        }
        catch { }
    }

    public static void Report(IlPromotionStore ours, int timeoutMs)
    {
        string head = $"=== {DateTime.UtcNow:HH:mm:ss.fff} pid {Environment.ProcessId}: a wait for promotions timed out after {timeoutMs} ms\n";
        try { Append(head + Snapshot(ours)); } catch (Exception ex) { Append(head + "  snapshot failed: " + ex + "\n"); }
        var t = new Thread(() =>
        {
            foreach (int s in new[] { 30, 60, 120 })
            {
                Thread.Sleep(s == 30 ? 30_000 : s == 60 ? 30_000 : 60_000);
                string later = $"--- {s} s after the timeout of {head.Substring(4, 12)}:\n";
                try { Append(later + Snapshot(ours)); } catch (Exception ex) { Append(later + "  snapshot failed: " + ex.Message + "\n"); }
            }
        }) { IsBackground = true, Name = "zzdiag" };
        t.Start();
    }
}

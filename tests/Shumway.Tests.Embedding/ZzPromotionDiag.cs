using System.Collections;
using System.Reflection;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// TEMPORARY diagnostic for the "promotion did not settle" flake: not for commit.
internal static class ZzPromotionDiag
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    private static string Field(object? target, Type type, string name)
    {
        var f = type.GetField(name, Any);
        if (f is null) return $"{name}=?";
        object? v = f.GetValue(f.IsStatic ? null : target);
        return v switch
        {
            ICollection c when name is "_emitTurnsDone" or "_pendingCompiles" or "_pendingCps"
                => $"{name}=[{string.Join(",", c.Cast<object>().Take(12).Select(x => x is int fid ? Name(fid) : x?.ToString()))}]",
            IDictionary d => $"{name}.keys=[{string.Join(",", d.Keys.Cast<object>().Take(12))}]",
            ICollection c => $"{name}.Count={c.Count}",
            _ => $"{name}={v}",
        };
    }

    private static string Name(int fid)
    {
        try { var (a, ar) = FunctorTable.Lookup(fid); return $"{AtomTable.GetById(a)?.Name}/{ar}"; }
        catch { return fid.ToString(); }
    }

    private static string Snapshot(PrologEngine e)
    {
        var st = e.IlPromotion;
        var w = typeof(IlCompileWorker);
        return IlCompileWorker.Describe() + " | "
            + string.Join(" ", new[] { "_busy", "_free", "_started", "_queue", "_lowQueue", "_syncQueue" }.Select(n => Field(null, w, n)))
            + " | " + string.Join(" ", new[] { "_emitTicketNext", "_emitTurnNow", "_deliverNext", "_undelivered", "_emitTurnsDone",
                "_pendingCompiles", "_pendingCps", "_completedCompiles" }.Select(n => Field(st, st.GetType(), n)));
    }

    // What a queued item's work captured: the promotion store that queued it
    // (an engine's), and the predicate it compiles, walking the closures two
    // levels down.
    private static void Captured(object? obj, int depth, ref object? store, ref string? pred, ref string kind)
    {
        if (obj is null || depth > 3) return;
        foreach (var f in obj.GetType().GetFields(Any))
        {
            if (f.IsStatic) continue;
            object? v;
            try { v = f.GetValue(obj); } catch { continue; }
            if (v is null) continue;
            if (store is null && v.GetType().Name == "IlPromotionStore") store = v;
            else if (pred is null && v is int fid && f.Name.Contains("functorId", StringComparison.OrdinalIgnoreCase)) pred = Name(fid);
            else if (pred is null && v.GetType().Name == "CompiledPredicate")
            {
                var p = v.GetType().GetProperty("FunctorId");
                if (p?.GetValue(v) is int fid2) pred = Name(fid2);
            }
            else if (v is Delegate d && depth < 3) { kind += d.Method.Name + ";"; Captured(d.Target, depth + 1, ref store, ref pred, ref kind); }
            else if (v.GetType().Name.Contains("DisplayClass") || v.GetType().Name.Contains("<>c")) Captured(v, depth + 1, ref store, ref pred, ref kind);
        }
    }

    private static string Queue(PrologEngine e)
    {
        var w = typeof(IlCompileWorker);
        var q = w.GetField("_queue", Any)!.GetValue(null)!;
        var items = ((IEnumerable)q.GetType().GetMethod("ToArray")!.Invoke(q, null)!).Cast<object>().ToList();
        var groups = new Dictionary<int, (int Count, int First, bool Ours, HashSet<string> Preds, HashSet<string> Kinds)>();
        for (int i = 0; i < items.Count; i++)
        {
            var work = items[i].GetType().GetField("Work", Any)?.GetValue(items[i]) as Delegate;
            object? store = null; string? pred = null; string kind = work?.Method.Name + ";";
            Captured(work?.Target, 0, ref store, ref pred, ref kind);
            int key = store is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(store);
            if (!groups.TryGetValue(key, out var g))
                g = (0, i, ReferenceEquals(store, e.IlPromotion), new HashSet<string>(), new HashSet<string>());
            g.Count++;
            if (pred is not null && g.Preds.Count < 10) g.Preds.Add(pred);
            if (g.Kinds.Count < 4) g.Kinds.Add(kind);
            groups[key] = g;
        }
        return $"queue {items.Count} items in {groups.Count} groups:\n" + string.Join("\n", groups.OrderByDescending(kv => kv.Value.Count).Take(8).Select(kv =>
            $"    store#{kv.Key:x}{(kv.Value.Ours ? " (OURS)" : "")}: {kv.Value.Count} items, first at {kv.Value.First}; preds [{string.Join(", ", kv.Value.Preds)}]; work [{string.Join(" | ", kv.Value.Kinds)}]"));
    }

    public static void Settle(PrologEngine e)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (e.IlPromotion.WaitForPendingPromotions(60_000)) return;
        string first = Snapshot(e), firstQueue;
        try { firstQueue = Queue(e); } catch (Exception ex) { firstQueue = "queue walk failed: " + ex.Message; }
        bool later = e.IlPromotion.WaitForPendingPromotions(120_000);
        string endQueue;
        try { endQueue = Queue(e); } catch (Exception ex) { endQueue = "queue walk failed: " + ex.Message; }
        Assert.Fail($"ZZDIAG not settled at {60_000} ms; then {(later ? "SETTLED" : "STILL NOT")} at {clock.ElapsedMilliseconds} ms\n"
            + $"  at 60 s: {first}\n  {firstQueue}\n  at end: {Snapshot(e)}\n  {endQueue}");
    }

    /// <summary>For a local check of the queue walk: a snapshot right now.</summary>
    public static string QueueNow(PrologEngine e) => Queue(e);
}

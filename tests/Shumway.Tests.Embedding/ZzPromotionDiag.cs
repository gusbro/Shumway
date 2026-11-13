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

    public static void Settle(PrologEngine e)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (e.IlPromotion.WaitForPendingPromotions(60_000)) return;
        string first = Snapshot(e);
        bool later = e.IlPromotion.WaitForPendingPromotions(120_000);
        Assert.Fail($"ZZDIAG not settled at {60_000} ms; then {(later ? "SETTLED" : "STILL NOT")} at {clock.ElapsedMilliseconds} ms\n"
            + $"  at 60 s: {first}\n  at end: {Snapshot(e)}");
    }
}

using System;
using System.Text;
using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>Forensics for the concurrent-bundle-build flake. A CI run produced
/// a bundle whose scenario answered <c>instantiation_error</c> in a fresh
/// process — a semantically broken bundle out of a build that ran beside other
/// builds. It has never reproduced locally, so the failure itself must carry
/// the evidence: rebuild the same bundle on the spot and compare STRUCTURALLY.
/// (Raw bytes cannot be compared: the persisted-IL blob embeds a fresh MVID
/// and PE timestamp per emit, so it always differs — benignly, in place, at
/// constant length. Everything else is deterministic, pinned by
/// <c>BundleBuild_BackToBack_IsStructurallyDeterministic</c>.)</summary>
internal static class BundleRaceDiag
{
    internal static string CompareWithRebuild(byte[] suspect, Func<byte[]> rebuild)
    {
        try
        {
            var b1 = BundleReader.FromBytes(suspect);
            var b2 = BundleReader.FromBytes(rebuild());
            return "[bundle-diag] suspect vs fresh rebuild:\n" + Structural(b1, b2)
                 + "  (a DIFFER in bytecode/patches/entries or an IL length change"
                 + " indicts the concurrent build; il=len-same is expected — MVID)";
        }
        catch (Exception ex)
        {
            return $"[bundle-diag] comparison threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    internal static string Structural(Bundle b1, Bundle b2)
    {
        var report = new StringBuilder();
        report.AppendLine($"  entries: {b1.Entries.Count} vs {b2.Entries.Count}");
        for (int i = 0; i < Math.Min(b1.Entries.Count, b2.Entries.Count); i++)
        {
            var (e1, e2) = (b1.Entries[i], b2.Entries[i]);
            report.AppendLine(
                $"  [{i}] {e1.ModuleName}/{e2.ModuleName}: "
                + $"bytecode={Same(e1.CompiledBytecode, e2.CompiledBytecode)} "
                + $"il={IlSame(e1.CompiledIl, e2.CompiledIl)} "
                + $"ilPatches={Same(e1.CompiledIlPatches, e2.CompiledIlPatches)} "
                + $"ilEntries={Same(e1.CompiledIlEntries, e2.CompiledIlEntries)} "
                + $"seeds={e1.DynamicSeeds.Count}vs{e2.DynamicSeeds.Count} "
                + $"src={(e1.Source == e2.Source ? "same" : "DIFFER")}");
        }
        return report.ToString();
    }

    private static string Same(byte[]? x, byte[]? y) =>
        (x is null) != (y is null) ? "NULL-MISMATCH"
        : x is null ? "both-null"
        : x.AsSpan().SequenceEqual(y) ? "same" : $"DIFFER({x.Length}vs{y!.Length})";

    /// The persisted-IL DLL legitimately differs per emit (MVID, PE timestamp)
    /// at CONSTANT length — only a length change is evidence.
    private static string IlSame(byte[]? x, byte[]? y) =>
        (x is null) != (y is null) ? "NULL-MISMATCH"
        : x is null ? "both-null"
        : x.Length == y!.Length ? "len-same" : $"LEN-DIFFER({x.Length}vs{y.Length})";
}

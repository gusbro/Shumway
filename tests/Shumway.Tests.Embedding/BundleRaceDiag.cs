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
                 + "  (a DIFFER in bytecode/patches/entries indicts the concurrent"
                 + " build; il compares BYTE RUNS — two or three short runs at"
                 + " fixed offsets are the MVID/PE-timestamp and benign, anything"
                 + " else is IL corruption in the build; il-clean = child-side)";
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
            // Spawner-side blob hashes: with SHUMWAY_BUNDLE_DIAG=1 the child
            // prints [bundle-diag-child] lines with the same hashes for what
            // it LOADED — match = transfer clean, corruption is child-side.
            report.AppendLine(
                $"      suspect hashes: il-pre={Hash(e1.CompiledIl)} "
                + $"patches={Hash(e1.CompiledIlPatches)} "
                + $"bytecode={Hash(e1.CompiledBytecode)} "
                + $"ilEntries={Hash(e1.CompiledIlEntries)}");
        }
        return report.ToString();
    }

    private static string Hash(byte[]? b) => b is null ? "null"
        : Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(b)).Substring(0, 8);

    private static string Same(byte[]? x, byte[]? y) =>
        (x is null) != (y is null) ? "NULL-MISMATCH"
        : x is null ? "both-null"
        : x.AsSpan().SequenceEqual(y) ? "same" : $"DIFFER({x.Length}vs{y!.Length})";

    /// The persisted-IL DLL legitimately differs per emit only in the MVID
    /// (16 bytes in the # GUID heap) and PE timestamp (4 bytes) — a couple of
    /// short byte runs. Anything beyond that is content corruption the old
    /// "len-same" verdict was blind to, so the runs themselves are the report:
    /// count, total bytes, and offsets (capped) for someone to line up against
    /// the PE layout.
    private static string IlSame(byte[]? x, byte[]? y)
    {
        if ((x is null) != (y is null)) return "NULL-MISMATCH";
        if (x is null) return "both-null";
        if (x.Length != y!.Length) return $"LEN-DIFFER({x.Length}vs{y.Length})";
        var runs = new System.Collections.Generic.List<(int Start, int Len)>();
        int totalDiff = 0;
        for (int i = 0; i < x.Length; )
        {
            if (x[i] == y[i]) { i++; continue; }
            int start = i;
            while (i < x.Length && x[i] != y[i]) i++;
            runs.Add((start, i - start));
            totalDiff += i - start;
        }
        if (runs.Count == 0) return "identical";
        var sb = new StringBuilder($"{runs.Count}runs/{totalDiff}B[");
        for (int i = 0; i < Math.Min(runs.Count, 8); i++)
            sb.Append(i > 0 ? "," : "").Append($"0x{runs[i].Start:X}+{runs[i].Len}");
        if (runs.Count > 8) sb.Append($",+{runs.Count - 8}more");
        sb.Append(']');
        // Heuristic verdict: MVID + timestamp shaped (few runs, few bytes) or not.
        sb.Append(runs.Count <= 4 && totalDiff <= 40 ? " (mvid-shaped)" : " IL-CONTENT-DIFFER");
        return sb.ToString();
    }
}

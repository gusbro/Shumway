using System.Text;

namespace Shumway.Embedding;

/// <summary>
/// Writes a <see cref="Bundle"/> to the on-disk Shumway bundle format
/// (see <see cref="BundleFormat"/> for the layout).
///
/// <para>The writer validates the bundle by running every entry through a
/// throwaway <see cref="PrologEngine"/>'s consult / first-query path. Any
/// parse or compile error surfaces here rather than at deployment.</para>
/// </summary>
public static class BundleWriter
{
    public static void WriteToFile(Bundle bundle, string path)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, ToBytes(bundle));
    }

    public static byte[] ToBytes(Bundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ValidateOrThrow(bundle);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(BundleFormat.Magic);
        bw.Write((uint)BundleFormat.CurrentVersion);
        bw.Write((uint)bundle.Entries.Count);
        foreach (var entry in bundle.Entries)
        {
            WriteLengthPrefixedUtf8(bw, entry.ModuleName);
            WriteLengthPrefixedUtf8(bw, entry.Source);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Compiles every entry through a fresh engine and runs a tiny
    /// dummy query so any unresolved-call or duplicate-public error fires.
    /// Throws on the first failure so callers (CLI / API) can surface a
    /// useful error message.</summary>
    private static void ValidateOrThrow(Bundle bundle)
    {
        var engine = new PrologEngine();
        foreach (var entry in bundle.Entries)
            engine.ConsultString(entry.Source);
        // Tickle the compile-once-per-query path so unresolved references
        // and public-uniqueness collisions surface now.
        engine.Query("true.");
    }

    private static void WriteLengthPrefixedUtf8(BinaryWriter bw, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        bw.Write((uint)bytes.Length);
        bw.Write(bytes);
    }
}

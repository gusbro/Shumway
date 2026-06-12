using System.Text;

namespace Shumway.Embedding;

/// <summary>
/// Serialises a <see cref="ShmoObject"/> to the on-disk
/// <c>.shmo</c> binary format (see <see cref="ShmoFormat"/>). Pure
/// I/O — does not consult, compile, or run any Prolog source. The
/// object is built upstream by <c>shumway-compile</c> (chunk 161).
/// </summary>
public static class ShmoWriter
{
    public static void WriteToFile(ShmoObject obj, string path)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, ToBytes(obj));
    }

    public static byte[] ToBytes(ShmoObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write(ShmoFormat.Magic);
        bw.Write((uint)ShmoFormat.CurrentVersion);
        WriteLengthPrefixedUtf8(bw, obj.ModuleName);
        WriteLengthPrefixedUtf8(bw, obj.Source);
        bw.Write((uint)obj.Bytecode.Length);
        bw.Write(obj.Bytecode);
        bw.Write((byte)obj.BuildMode);   // V2+
        bw.Write((byte)(obj.ArityCompat ? 1 : 0));   // chunk 441

        bw.Write((uint)obj.Defined.Count);
        foreach (var d in obj.Defined)
        {
            WriteLengthPrefixedUtf8(bw, d.Indicator.Name);
            bw.Write((uint)d.Indicator.Arity);
            bw.Write((byte)d.Visibility);
        }

        bw.Write((uint)obj.EnsureLinked.Count);
        foreach (var p in obj.EnsureLinked)
        {
            WriteLengthPrefixedUtf8(bw, p.Name);
            bw.Write((uint)p.Arity);
        }

        bw.Write((uint)obj.CallGraph.Count);
        foreach (var kv in obj.CallGraph)
        {
            WriteLengthPrefixedUtf8(bw, kv.Key.Name);
            bw.Write((uint)kv.Key.Arity);
            bw.Write((uint)kv.Value.Count);
            foreach (var edge in kv.Value)
            {
                WriteLengthPrefixedUtf8(bw, edge.Target.Name);
                bw.Write((uint)edge.Target.Arity);
                bw.Write((byte)(edge.IsMeta ? 1 : 0));   // chunk 441
            }
        }

        bw.Write((uint)obj.QualifiedRefs.Count);
        foreach (var q in obj.QualifiedRefs)
        {
            WriteLengthPrefixedUtf8(bw, q.Module);
            WriteLengthPrefixedUtf8(bw, q.Name);
            bw.Write((uint)q.Arity);
        }

        // dynamicSeeds trailer.
        bw.Write((uint)obj.DynamicSeeds.Count);
        foreach (var seed in obj.DynamicSeeds)
        {
            WriteLengthPrefixedUtf8(bw, seed.Indicator.Name);
            bw.Write((uint)seed.Indicator.Arity);
            bw.Write((uint)seed.EncodedClauses.Count);
            foreach (var encoded in seed.EncodedClauses)
            {
                bw.Write((uint)encoded.Length);
                bw.Write(encoded);
            }
        }

        // clauseTerms trailer (the LTO channel — raw static clauses).
        bw.Write((uint)obj.ClauseTerms.Count);
        foreach (var encoded in obj.ClauseTerms)
        {
            bw.Write((uint)encoded.Length);
            bw.Write(encoded);
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteLengthPrefixedUtf8(BinaryWriter bw, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        bw.Write((uint)bytes.Length);
        bw.Write(bytes);
    }
}

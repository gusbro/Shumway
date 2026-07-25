using System.Text;

namespace Shumway.Embedding;

/// <summary>
/// Serialises a <see cref="ShmoObject"/> to the on-disk
/// <c>.shmo</c> binary format (see <see cref="ShmoFormat"/>). Pure
/// I/O — does not consult, compile, or run any Prolog source. The
/// object is built upstream by <c>shumway-compile</c>.
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

        // the body after magic+version is finalized through the
        // same raw-or-Brotli framing as the .shum (BundleFormat.FinalizeImage;
        // one flag byte at offset 8). The big .shmo sections — bytecode and
        // the ClauseTerms LTO trailer (measured 43.7% + 37.7% of real-corpus
        // objects) — compress well, and .shmo files are only ever read by our
        // own tools.
        bw.Write(ShmoFormat.Magic);
        bw.Write((uint)ShmoFormat.CurrentVersion);
        WriteLengthPrefixedUtf8(bw, obj.ModuleName);
        WriteLengthPrefixedUtf8(bw, obj.Source);
        bw.Write((uint)obj.Bytecode.Length);
        bw.Write(obj.Bytecode);
        bw.Write((byte)obj.BuildMode);   // V2+
        bw.Write((byte)(obj.ArityCompat ? 1 : 0));

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
                bw.Write((byte)(edge.IsMeta ? 1 : 0));
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
            bw.Write(seed.Multifile);
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

        // nativeBlocks trailer (ADR-022 — embedded native-block marshalling).
        bw.Write((uint)obj.NativeBlocks.Count);
        foreach (var nb in obj.NativeBlocks)
        {
            WriteLengthPrefixedUtf8(bw, nb.Name);
            WriteLengthPrefixedUtf8(bw, nb.RawText);
            bw.Write((uint)nb.Vars.Count);
            foreach (var v in nb.Vars)
            {
                WriteLengthPrefixedUtf8(bw, v.Name);
                bw.Write((byte)v.Kind);
                bw.Write((byte)v.Mode);
            }
            // ADR-022 — scalar `:- c` globals (name + is-float).
            bw.Write((uint)nb.ScalarGlobals.Count);
            foreach (var g in nb.ScalarGlobals)
            {
                WriteLengthPrefixedUtf8(bw, g.Name);
                bw.Write(g.IsFloat);
            }
        }

        // ADR-024 — native-interop trailer: :- native indicators + :- c decls.
        bw.Write((uint)obj.NativeFunctions.Count);
        foreach (var pr in obj.NativeFunctions)
        {
            WriteLengthPrefixedUtf8(bw, pr.Name);
            bw.Write((uint)pr.Arity);
        }
        WriteLengthPrefixedUtf8(bw, obj.NativeDecls ?? string.Empty);

        // Operator trailer: the `:- op/3` definitions
        // this module's source executed, replayed at LoadBundle so stripped
        // bundles keep their runtime operator table.
        bw.Write((uint)obj.Operators.Count);
        foreach (var od in obj.Operators)
        {
            bw.Write(od.Priority);
            WriteLengthPrefixedUtf8(bw, od.Type);
            WriteLengthPrefixedUtf8(bw, od.Name);
        }

        // ADR-038 — export-qualification trailer: the export surface, resolved
        // import table, and library dependencies of a :- module/2 module.
        bw.Write(obj.IsExportQualified);
        bw.Write((uint)obj.Exports.Count);
        foreach (var p in obj.Exports)
        {
            WriteLengthPrefixedUtf8(bw, p.Name);
            bw.Write((uint)p.Arity);
        }
        bw.Write((uint)obj.Imports.Count);
        foreach (var imp in obj.Imports)
        {
            WriteLengthPrefixedUtf8(bw, imp.Pred.Name);
            bw.Write((uint)imp.Pred.Arity);
            WriteLengthPrefixedUtf8(bw, imp.Source);
        }
        bw.Write((uint)obj.LibraryDeps.Count);
        foreach (var dep in obj.LibraryDeps)
        {
            WriteLengthPrefixedUtf8(bw, dep.LibName);
            bw.Write(dep.Baked);
            bw.Write(dep.Filter is null);   // true = import-all (null filter)
            if (dep.Filter is not null)
            {
                bw.Write((uint)dep.Filter.Count);
                foreach (var p in dep.Filter)
                {
                    WriteLengthPrefixedUtf8(bw, p.Name);
                    bw.Write((uint)p.Arity);
                }
            }
        }

        bw.Flush();
        return BundleFormat.FinalizeImage(ms.ToArray());
    }

    private static void WriteLengthPrefixedUtf8(BinaryWriter bw, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        bw.Write((uint)bytes.Length);
        bw.Write(bytes);
    }
}

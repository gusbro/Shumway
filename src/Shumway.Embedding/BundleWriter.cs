using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;

namespace Shumway.Embedding;

/// <summary>
/// Writes a <see cref="Bundle"/> to the on-disk Shumway bundle format
/// (see <see cref="BundleFormat"/> for the layout).
///
/// <para>The writer validates the bundle by running every entry through a
/// throwaway <see cref="PrologEngine"/>'s consult / first-query path. Any
/// parse or compile error surfaces here rather than at deployment.</para>
///
/// <para>Set <c>includeCompiledBytecode</c> to embed a compiled-bytecode
/// payload for each entry (produced via <see cref="CompiledModuleCodec"/>).
/// Bundles produced without that flag still round-trip correctly; their
/// entries simply expose <c>CompiledBytecode == null</c>.</para>
/// </summary>
public static class BundleWriter
{
    public static void WriteToFile(Bundle bundle, string path, bool includeCompiledBytecode = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, ToBytes(bundle, includeCompiledBytecode));
    }

    public static byte[] ToBytes(Bundle bundle, bool includeCompiledBytecode = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ValidateOrThrow(bundle);

        // If the caller asked for compiled blobs and an entry doesn't already
        // carry one, synthesise it now from the source — keeping the writer
        // ergonomic for hand-built bundles (the typical case in tests / CLI).
        BundleEntry[] effective = bundle.Entries.ToArray();
        if (includeCompiledBytecode)
        {
            for (int i = 0; i < effective.Length; i++)
                if (effective[i].CompiledBytecode is null)
                    effective[i] = new BundleEntry(
                        effective[i].ModuleName,
                        effective[i].Source,
                        CompileEntryToBytes(effective[i].Source));
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(BundleFormat.Magic);
        bw.Write((uint)BundleFormat.CurrentVersion);
        bw.Write((uint)effective.Length);
        foreach (var entry in effective)
        {
            WriteLengthPrefixedUtf8(bw, entry.ModuleName);
            WriteLengthPrefixedUtf8(bw, entry.Source);
            byte[] compiled = entry.CompiledBytecode ?? Array.Empty<byte>();
            bw.Write((uint)compiled.Length);
            bw.Write(compiled);
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

    /// <summary>Parses one module's source, compiles its clauses, and
    /// encodes the resulting <see cref="CompiledModule"/> into the codec's
    /// portable byte form. Mirrors the parser-then-compile pipeline that
    /// <see cref="PrologEngine.ConsultString"/> would use, minus the
    /// module-aware mangling — the bundle stores per-module compiled
    /// output and re-mangles on consult.</summary>
    private static byte[] CompileEntryToBytes(string source)
    {
        // The shared builtins need to be registered before the WAM compiler
        // can resolve calls like `is/2` and `=/2`. EnsureRegistered is
        // idempotent, so calling it from the writer doesn't disturb any
        // engine the host happens to have spun up.
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();

        var clauses = new ClauseReader(new Lexer(source), OperatorTable.Default())
            .ReadAll()
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        var module = new ModuleCompiler().Compile(clauses);
        return CompiledModuleCodec.Encode(module);
    }

    private static void WriteLengthPrefixedUtf8(BinaryWriter bw, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        bw.Write((uint)bytes.Length);
        bw.Write(bytes);
    }
}

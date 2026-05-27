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
    public static void WriteToFile(Bundle bundle, string path,
        bool includeCompiledBytecode = false,
        bool includeCompiledIl = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, ToBytes(bundle, includeCompiledBytecode, includeCompiledIl));
    }

    public static byte[] ToBytes(Bundle bundle,
        bool includeCompiledBytecode = false,
        bool includeCompiledIl = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ValidateOrThrow(bundle);

        // If the caller asked for compiled blobs and an entry doesn't already
        // carry one, synthesise it now from the source — keeping the writer
        // ergonomic for hand-built bundles (the typical case in tests / CLI).
        BundleEntry[] effective = bundle.Entries.ToArray();
        if (includeCompiledBytecode || includeCompiledIl)
        {
            for (int i = 0; i < effective.Length; i++)
            {
                byte[]? compiledBytecode = effective[i].CompiledBytecode;
                byte[]? compiledIl = effective[i].CompiledIl;
                if (includeCompiledBytecode && compiledBytecode is null)
                    compiledBytecode = CompileEntryToBytes(effective[i].Source);
                if (includeCompiledIl && compiledIl is null)
                    compiledIl = CompileEntryToIl(effective[i]);
                effective[i] = new BundleEntry(
                    effective[i].ModuleName,
                    effective[i].Source,
                    compiledBytecode,
                    compiledIl,
                    effective[i].Defined);
            }
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
            byte[] compiledIl = entry.CompiledIl ?? Array.Empty<byte>();
            bw.Write((uint)compiledIl.Length);
            bw.Write(compiledIl);
            // V2+: per-predicate visibility metadata. Empty list is fine —
            // the source-less load path only fires when this is non-empty
            // AND Source is stripped.
            bw.Write((uint)entry.Defined.Count);
            foreach (var d in entry.Defined)
            {
                WriteLengthPrefixedUtf8(bw, d.Indicator.Name);
                bw.Write((uint)d.Indicator.Arity);
                bw.Write((byte)d.Visibility);
            }
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Compiles <paramref name="entry"/>'s source through the WAM
    /// pipeline, then through
    /// <see cref="Shumway.Compiler.Il.PersistedIlBuilder"/> to produce a
    /// .NET assembly containing one static method per IL-eligible
    /// predicate. The resulting .dll bytes embed into the bundle and the
    /// load path uses them to bind <c>PredicateDelegate</c>s without
    /// re-running the Sigil pipeline at consult time.</summary>
    private static byte[] CompileEntryToIl(BundleEntry entry)
    {
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
        // Run through a full PrologEngine.ConsultString + warm-up so the
        // module rewriter, dynamic-functor routing, prelude, and per-query
        // synthetic launcher all agree on which functor ids end up
        // representing each predicate. PersistedIlBuilder then sees the
        // same CompiledPredicate the runtime path would compile.
        var engine = new Shumway.Embedding.PrologEngine();
        engine.ConsultString(entry.Source);
        engine.Query("true.");
        // Pull every IL-eligible predicate the warm-up populated.
        // Static (chunk 82) covers immutable user clauses; dynamic
        // (chunk 68) covers `:- dynamic`-declared ones. The
        // PrecompiledClauseCache (chunk 53) is bundle-load only —
        // useless here because we're the *builder* not a loader.
        var predicates = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>();
        foreach (var (fid, pred) in engine.StaticPredicateCache)
            predicates[fid] = pred;
        foreach (var (fid, pred) in engine.DynamicPredicateCache)
            predicates[fid] = pred;
        // Caches still empty? Fall through to an empty assembly
        // (the load path simply finds no methods to bind).
        var (dllBytes, _) = Shumway.Compiler.Il.PersistedIlBuilder.Build(
            "ShumwayCompiledIl_" + SanitiseModuleName(entry.ModuleName),
            predicates);
        return dllBytes;
    }

    private static string SanitiseModuleName(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
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

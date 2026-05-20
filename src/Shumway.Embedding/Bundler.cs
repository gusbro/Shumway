using System.Text.RegularExpressions;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// Chunk 72 — programmatic entry point for the bundling pipeline.
/// ADR-009 sketched a <c>Bundler</c> class for build-pipeline /
/// MSBuild / CI integration alongside the <c>shumway-bundler</c> CLI;
/// chunk 72 lands it. The CLI is now a thin wrapper around
/// <see cref="Build"/>.
///
/// <para>The API accepts source files on disk, in-memory module
/// entries, or both. It validates each module by running it through
/// a throwaway <see cref="PrologEngine"/>, optionally embeds compiled
/// bytecode (chunk 45) and a compiled-IL .dll (chunk 71), and
/// returns a structured <see cref="BundleResult"/> with diagnostics
/// (errors, warnings, informational notes), the in-memory
/// <see cref="Bundle"/>, the serialised bytes, and a
/// <see cref="BundleReport"/> for tooling consumers.</para>
/// </summary>
public static class Bundler
{
    /// <summary>Synchronous entry point. Always returns a
    /// <see cref="BundleResult"/>; errors land in
    /// <see cref="BundleResult.Diagnostics"/> rather than throwing.
    /// Programmatic callers check <see cref="BundleResult.Success"/>
    /// and inspect the diagnostics. Unexpected exceptions (e.g. I/O
    /// faults outside the source-file-loading step) still propagate
    /// — they indicate environment problems, not bundle errors.</summary>
    public static BundleResult Build(BundleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var diagnostics = new List<BundleDiagnostic>();

        // ----- Collect source entries -----
        var entries = new List<BundleEntry>(
            config.SourceFiles.Count + config.InlineEntries.Count);
        foreach (string file in config.SourceFiles)
        {
            if (!File.Exists(file))
            {
                diagnostics.Add(new BundleDiagnostic(
                    BundleSeverity.Error, $"source file not found: {file}", Source: file));
                continue;
            }
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new BundleDiagnostic(
                    BundleSeverity.Error, $"failed to read {file}: {ex.Message}", Source: file));
                continue;
            }
            string moduleName = ExtractModuleName(source)
                ?? Path.GetFileNameWithoutExtension(file);
            entries.Add(new BundleEntry(moduleName, source));
        }
        foreach (var inline in config.InlineEntries) entries.Add(inline);
        if (entries.Count == 0)
        {
            diagnostics.Add(new BundleDiagnostic(
                BundleSeverity.Error,
                "no source files or inline entries supplied to the bundler."));
            return new BundleResult { Success = false, Diagnostics = diagnostics };
        }
        if (HasErrors(diagnostics))
            return new BundleResult { Success = false, Diagnostics = diagnostics };

        if (config.Verbose && config.VerboseOut is not null)
        {
            config.VerboseOut.WriteLine($"shumway-bundler: {entries.Count} module(s) staged.");
            foreach (var e in entries)
                config.VerboseOut.WriteLine(
                    $"  - {e.ModuleName} ({e.Source.Length} chars)");
        }

        // ----- Build + serialise -----
        var inputBundle = new Bundle(entries);
        byte[] bytes;
        Bundle bundle;
        try
        {
            bytes = BundleWriter.ToBytes(inputBundle,
                includeCompiledBytecode: config.IncludeCompiledBytecode,
                includeCompiledIl: config.IncludeCompiledIl);
            // When the writer synthesised compiled blobs from source, the
            // input Bundle's entries don't carry them — round-trip through
            // BundleReader to surface the effective entries (with blobs) so
            // BundleResult.Bundle is what's actually on disk.
            bundle = (config.IncludeCompiledBytecode || config.IncludeCompiledIl)
                ? BundleReader.FromBytes(bytes)
                : inputBundle;
        }
        catch (Exception ex)
        {
            diagnostics.Add(new BundleDiagnostic(
                BundleSeverity.Error, $"bundle failed: {ex.Message}"));
            return new BundleResult { Success = false, Diagnostics = diagnostics };
        }

        // ----- Entry-point validation -----
        if (config.EntryPoints.Count > 0)
        {
            var engine = new PrologEngine();
            try
            {
                engine.LoadBundle(bundle);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new BundleDiagnostic(
                    BundleSeverity.Error, $"entry-point validation: bundle load failed: {ex.Message}"));
                return new BundleResult { Success = false, Diagnostics = diagnostics };
            }
            foreach (var ep in config.EntryPoints)
            {
                if (!CheckEntryPointExists(engine, ep, out string error))
                {
                    diagnostics.Add(new BundleDiagnostic(
                        BundleSeverity.Error,
                        $"entry-point check failed: {ep.Name}/{ep.Arity}: {error}"));
                }
            }
            if (HasErrors(diagnostics))
                return new BundleResult { Success = false, Diagnostics = diagnostics };
        }

        // ----- Write to disk (if requested) -----
        if (config.OutputPath is not null)
        {
            try
            {
                File.WriteAllBytes(config.OutputPath, bytes);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new BundleDiagnostic(
                    BundleSeverity.Error,
                    $"failed to write {config.OutputPath}: {ex.Message}"));
                return new BundleResult { Success = false, Diagnostics = diagnostics };
            }
            if (config.Verbose && config.VerboseOut is not null)
                config.VerboseOut.WriteLine($"shumway-bundler: wrote {config.OutputPath}.");
        }

        // ----- Build report -----
        var report = BuildReport(bundle, bytes);

        return new BundleResult
        {
            Success = true,
            Bundle = bundle,
            Bytes = bytes,
            Diagnostics = diagnostics,
            Report = report,
        };
    }

    /// <summary>Async wrapper around <see cref="Build"/>. The work is
    /// CPU-bound (parser + WAM compiler + optional IL emission), so
    /// this just shifts the call onto the thread pool — callers in
    /// async contexts get a Task they can await without blocking
    /// their UI / request thread. Cancellation is honoured at the
    /// task-launch boundary; mid-build cancellation isn't supported
    /// in v1 because the compiler doesn't take a CancellationToken.</summary>
    public static Task<BundleResult> BuildAsync(
        BundleConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Build(config);
        }, cancellationToken);
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private static bool HasErrors(List<BundleDiagnostic> diagnostics)
    {
        foreach (var d in diagnostics) if (d.Severity == BundleSeverity.Error) return true;
        return false;
    }

    private static bool CheckEntryPointExists(
        PrologEngine engine, EntryPointSpec ep, out string error)
    {
        // Build a goal that calls ep with anonymous variables and falls
        // back to true on failure — just verifies the call resolves
        // without binding anything.
        string goalArgs = ep.Arity == 0
            ? ""
            : "(" + string.Join(", ", Enumerable.Range(0, ep.Arity).Select(_ => "_")) + ")";
        string probe = $"({ep.Name}{goalArgs} ; true).";
        try
        {
            engine.Query(probe);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ExtractModuleName(string source)
    {
        var m = Regex.Match(source,
            @"^\s*:-\s*module\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\.",
            RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static BundleReport BuildReport(Bundle bundle, byte[] bytes)
    {
        int totalSource = 0, totalBytecode = 0, totalIl = 0;
        var modules = new List<string>(bundle.Entries.Count);
        foreach (var entry in bundle.Entries)
        {
            modules.Add(entry.ModuleName);
            totalSource += entry.Source.Length;
            totalBytecode += entry.CompiledBytecode?.Length ?? 0;
            totalIl += entry.CompiledIl?.Length ?? 0;
        }
        return new BundleReport
        {
            ModuleCount = bundle.Entries.Count,
            TotalSourceChars = totalSource,
            CompiledBytecodeBytes = totalBytecode,
            CompiledIlBytes = totalIl,
            BundleBytes = bytes.Length,
            Modules = modules,
        };
    }
}

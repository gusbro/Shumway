namespace Shumway.Embedding;

/// <summary>
/// Configuration for a single <see cref="Bundler.Build"/> call.
/// Source modules can come from either <see cref="SourceFiles"/> (paths
/// on disk; the bundler reads them) or <see cref="InlineEntries"/>
/// (already-loaded <see cref="BundleEntry"/> instances). Both are
/// allowed; inline entries follow file entries in the resulting bundle.
///
/// <para>The defaults match the chunk-22 CLI's behaviour: no compiled
/// blobs, no entry-point validation, write nothing to disk. Each
/// non-empty option opts the call into a specific bundler feature.</para>
/// </summary>
public sealed class BundleConfig
{
    /// <summary>Paths on disk that the bundler reads as module sources.
    /// Order is preserved in the resulting bundle. Missing files
    /// surface as <see cref="BundleSeverity.Error"/> diagnostics.</summary>
    public IList<string> SourceFiles { get; init; } = new List<string>();

    /// <summary>Pre-built module entries. Useful when the caller has
    /// already loaded source from a custom source (e.g. an embedded
    /// resource, a generated string, an upstream bundle).</summary>
    public IList<BundleEntry> InlineEntries { get; init; } = new List<BundleEntry>();

    /// <summary>Destination file path. When non-null, the serialised
    /// bundle is written there. When null, the bundle stays in memory
    /// — <see cref="BundleResult.Bytes"/> still carries the bytes for
    /// callers that ship the bundle through another channel.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Predicates whose existence the bundler should verify
    /// after writing. Each is loaded through a throwaway engine and a
    /// probing goal is run. Missing or unresolvable predicates surface
    /// as <see cref="BundleSeverity.Error"/> diagnostics.</summary>
    public IList<EntryPointSpec> EntryPoints { get; init; } = new List<EntryPointSpec>();

    /// <summary>Embed pre-compiled WAM bytecode alongside the source
    /// (chunk 45). LoadBundle uses it to pre-warm Tier-1 IL so the
    /// first call into each eligible predicate already hits IL.</summary>
    public bool IncludeCompiledBytecode { get; init; }

    /// <summary>Embed a persisted .NET assembly holding Tier-1 IL for
    /// every IL-eligible predicate (chunk 71). LoadBundle binds methods
    /// directly without re-running the Sigil emit at consult time.</summary>
    public bool IncludeCompiledIl { get; init; }

    /// <summary>When true, the bundler writes progress notes to
    /// <see cref="VerboseOut"/>. Diagnostics always go through
    /// <see cref="BundleResult.Diagnostics"/>; verbose output is for
    /// progress / hint info that doesn't surface as a result entry.</summary>
    public bool Verbose { get; init; }

    /// <summary>Destination for verbose progress output. When null
    /// (the default), verbose lines are silently dropped.</summary>
    public TextWriter? VerboseOut { get; init; }
}

/// <summary>Predicate indicator used by
/// <see cref="BundleConfig.EntryPoints"/>. Mirrors the
/// <c>Name/Arity</c> Prolog notation.</summary>
public readonly record struct EntryPointSpec(string Name, int Arity);

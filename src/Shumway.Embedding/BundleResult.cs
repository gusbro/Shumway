namespace Shumway.Embedding;

/// <summary>
/// Outcome of a <see cref="Bundler.Build"/> call. Tools / build
/// systems inspect <see cref="Success"/> first, then
/// <see cref="Diagnostics"/> for the per-message detail; the
/// <see cref="Report"/> summarises what was bundled for progress
/// readouts.
/// </summary>
public sealed class BundleResult
{
    /// <summary>True iff every step (source loading, validation,
    /// serialisation, entry-point check, optional disk write)
    /// completed without an <see cref="BundleSeverity.Error"/>
    /// diagnostic. Warnings and informational notes don't affect
    /// this value.</summary>
    public required bool Success { get; init; }

    /// <summary>The in-memory <see cref="Bundle"/> the bundler built.
    /// Populated on success; null when a pre-build error stopped the
    /// pipeline before the bundle was materialised.</summary>
    public Bundle? Bundle { get; init; }

    /// <summary>The serialised bundle bytes. Populated on success and
    /// when the bundler made it as far as serialisation (it's possible
    /// for entry-point validation or output-write to fail with bytes
    /// already in hand — the caller can still ship them).</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Per-step diagnostics, in the order the bundler
    /// produced them. Errors stop the pipeline at known checkpoints
    /// (source load, serialise, entry-point check, disk write).
    /// Warnings and infos are advisory — the build proceeds.</summary>
    public IReadOnlyList<BundleDiagnostic> Diagnostics { get; init; }
        = Array.Empty<BundleDiagnostic>();

    /// <summary>Summary statistics about the produced bundle —
    /// module count, byte sizes, etc. Populated only on success.</summary>
    public BundleReport? Report { get; init; }

    /// <summary>Filter helper: the diagnostics whose severity is
    /// <see cref="BundleSeverity.Error"/>.</summary>
    public IEnumerable<BundleDiagnostic> Errors =>
        Diagnostics.Where(d => d.Severity == BundleSeverity.Error);

    /// <summary>Filter helper: the diagnostics whose severity is
    /// <see cref="BundleSeverity.Warning"/>.</summary>
    public IEnumerable<BundleDiagnostic> Warnings =>
        Diagnostics.Where(d => d.Severity == BundleSeverity.Warning);
}

/// <summary>One diagnostic from the bundling pipeline. Severity
/// determines whether the bundle build succeeded; the message is
/// human-readable; <see cref="Source"/> optionally names a source
/// file or module the diagnostic relates to.</summary>
public sealed record BundleDiagnostic(
    BundleSeverity Severity,
    string Message,
    string? Source = null);

/// <summary>Severity bands. Errors fail the build; warnings and infos
/// are advisory.</summary>
public enum BundleSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Summary statistics over a successfully built bundle.
/// Useful for progress UIs and CI reports.</summary>
public sealed class BundleReport
{
    public required int ModuleCount { get; init; }
    public required int TotalSourceChars { get; init; }
    public required int CompiledBytecodeBytes { get; init; }
    public required int CompiledIlBytes { get; init; }
    public required int BundleBytes { get; init; }
    public required IReadOnlyList<string> Modules { get; init; }
}

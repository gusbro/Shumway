namespace Shumway.Core;

/// <summary>
/// Tunable parameters for an <see cref="Activation"/>. Sizes are in <see cref="Cell"/>
/// units (or <see cref="ExtraTrailEntry"/> for the extra trail, <c>int</c> for the
/// binding trail). A maximum of <c>0</c> means unlimited (the engine still throws
/// on .NET array-size limits, naturally).
/// </summary>
public sealed class ActivationConfig
{
    public int InitialHeapSize { get; init; } = 65536;

    /// <summary>The heap's maximum in cells; past it an allocation raises
    /// <c>resource_error(memory)</c>. The adaptive collection trigger
    /// (ADR-016) spaces collections within it; when it is <c>0</c> the
    /// trigger takes half the memory the runtime reports available.</summary>
    public int MaxHeapSize { get; init; }

    public int InitialStackSize { get; init; } = 8192;
    public int MaxStackSize { get; init; }

    public int InitialBindingTrailSize { get; init; } = 1024;
    public int MaxBindingTrailSize { get; init; }

    public int InitialExtraTrailSize { get; init; } = 64;
    public int MaxExtraTrailSize { get; init; }

    /// <summary>The register bank's initial size; never below
    /// <see cref="Activation.MinRegisterCount"/>.</summary>
    public int InitialRegisterCount { get; init; } = 64;
    public int MaxRegisterCount { get; init; }

    /// <summary>ADR-016: heap occupancy (in cells) at which the engine runs
    /// its first mark-compact collection, at the next safe point. After each
    /// collection the next threshold is what survived plus a margin chosen
    /// from how much the last collections recovered (the adaptive trigger,
    /// ADR-016's 2026-11 amendment); this value is the margin's floor.
    /// <c>0</c> disables automatic collection (explicit
    /// <c>garbage_collect/0</c> still works).
    ///
    /// <para>Default <c>1&lt;&lt;20</c> (1 M cells, 8 MB). Trap: control
    /// words (notably the <c>get_level</c> cut barrier) must be
    /// <c>Tag.RawInt</c>-tagged, or the conservative stack scan relocates
    /// them as heap refs.</para></summary>
    public int GcThreshold { get; init; } = 1 << 20;
}

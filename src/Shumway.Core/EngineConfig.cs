namespace Shumway.Core;

/// <summary>
/// Tunable parameters for an <see cref="Engine"/>. Sizes are in <see cref="Cell"/>
/// units (or <see cref="ExtraTrailEntry"/> for the extra trail, <c>int</c> for the
/// binding trail). A maximum of <c>0</c> means unlimited (the engine still throws
/// on .NET array-size limits, naturally).
/// </summary>
public sealed class EngineConfig
{
    public int InitialHeapSize { get; init; } = 65536;
    public int MaxHeapSize { get; init; }

    public int InitialStackSize { get; init; } = 8192;
    public int MaxStackSize { get; init; }

    public int InitialBindingTrailSize { get; init; } = 1024;
    public int MaxBindingTrailSize { get; init; }

    public int InitialExtraTrailSize { get; init; } = 64;
    public int MaxExtraTrailSize { get; init; }

    public int InitialRegisterCount { get; init; } = 64;
    public int MaxRegisterCount { get; init; }

    /// <summary>ADR-016 — heap occupancy (in cells) at which the engine
    /// runs a mark-compact collection at the next safe point, before
    /// growing the heap array. After each collection the threshold is
    /// raised to twice the surviving live size (so a genuinely large live
    /// set does not trigger repeated futile collections). <c>0</c>
    /// disables automatic collection (explicit <c>garbage_collect/0</c>
    /// still works).
    ///
    /// <para>Default <c>1&lt;&lt;18</c> (256 K cells ≈ 2 MB). Auto-collection
    /// was held off through chunk 212 while the SHUMWAY_GC_STRESS fuzz
    /// surfaced a missing root in the tabling / meta-call machinery; chunk
    /// 213 traced it to control words (notably the <c>get_level</c> cut
    /// barrier) stored as <c>Tag.Ref</c> and relocated by the conservative
    /// stack scan, fixed it with <c>Tag.RawInt</c>-tagged control words,
    /// and re-enabled auto-collection.</para></summary>
    public int GcThreshold { get; init; } = 1 << 18;
}

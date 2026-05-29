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
    /// <para>Default is currently <c>0</c> (off). The collector and the
    /// safe-point wiring are correct for plain execution, but the
    /// SHUMWAY_GC_STRESS fuzz surfaced a missing root in the tabling /
    /// meta-call machinery (a goal aliased to a <c>-/2</c> answer-table
    /// pair). Auto-collection stays off until that root is found; the
    /// stress harness is the reproducer.</para></summary>
    public int GcThreshold { get; init; }   // 0 = disabled (see remarks)
}

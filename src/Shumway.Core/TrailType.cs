namespace Shumway.Core;

/// <summary>
/// Discriminator for <see cref="ExtraTrailEntry"/>. The numeric ranges leave room
/// for future categories without renumbering existing entries (per ADR-004).
/// </summary>
public enum TrailType : byte
{
    /// <summary>An already-bound cell was overwritten with a different value.</summary>
    ValueChange = 1,

    /// <summary>Retired by ADR-064: a big integer is a block of heap cells,
    /// reclaimed by the heap top reset, so nothing records one. The code
    /// stays because a wasm module bakes these codes into its trail
    /// compaction; an entry of this kind is never produced and unwinds as a
    /// no-op.</summary>
    BigIntAlloc = 2,

    /// <summary>One slot was appended to the rational side table (ADR-039).
    /// Same contract as <see cref="BigIntAlloc"/>: the entry's
    /// <see cref="ExtraTrailEntry.HeapIdx"/> is the table size before the
    /// append, so unwind truncates the table back and reclaims the slot.</summary>
    RationalAlloc = 3,

    // 16..31 reserved for attributed-variable operations.
    AttrAdd = 16,
    AttrModify = 17,
    AttrRemove = 18,

    // 32..63 reserved for mutable globals (b_setval/2 etc.).
    MutableSet = 32,

    // 64..79 reserved for control / exception state.

    /// <summary>A catch-frame stack operation (push or deactivate) made by
    /// <c>'$catch_begin'</c> / <c>'$catch_end'</c>. The entry's
    /// <see cref="ExtraTrailEntry.HeapIdx"/> field carries the catch-frame
    /// index and <see cref="ExtraTrailEntry.OldValue"/> the operation to
    /// reverse, so backtracking past a catch/3 restores the frame stack —
    /// see <see cref="Activation"/>'s catch-frame methods.</summary>
    CatchFrame = 64,
}

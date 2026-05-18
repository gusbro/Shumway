namespace Shumway.Core;

/// <summary>
/// Discriminator for <see cref="ExtraTrailEntry"/>. The numeric ranges leave room
/// for future categories without renumbering existing entries (per ADR-004).
/// </summary>
public enum TrailType : byte
{
    /// <summary>An already-bound cell was overwritten with a different value.</summary>
    ValueChange = 1,

    /// <summary>One slot was appended to the BigInteger side table; the
    /// trail entry's <see cref="ExtraTrailEntry.HeapIdx"/> field carries
    /// the table size <em>before</em> the append, so unwind can truncate
    /// the table back to that size and reclaim the slot. Lets backtracking
    /// past a <c>is/2</c> that produced a transient BigInteger free the
    /// side-table slot instead of leaving it dangling.</summary>
    BigIntAlloc = 2,

    // 16..31 reserved for attributed-variable operations (Phase 4).
    AttrAdd = 16,
    AttrModify = 17,
    AttrRemove = 18,

    // 32..63 reserved for mutable globals (b_setval/2 etc., optional Phase 2+).
    MutableSet = 32,
}

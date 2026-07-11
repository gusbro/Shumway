namespace Shumway.Core;

/// <summary>
/// One entry on the engine's <see cref="Activation.ExtraTrailTop">extra trail</see>, used
/// for non-binding reversible state changes. Bindings themselves use the cheaper
/// <c>int[]</c> binding trail; see ADR-004 for the rationale.
///
/// <para><see cref="BindingTrailMarker"/> records the binding-trail top at the moment
/// this entry was pushed, so unwind can interleave binding and value-change rollbacks
/// in the correct order.</para>
/// </summary>
public struct ExtraTrailEntry
{
    public TrailType Type;
    public int HeapIdx;
    public Cell OldValue;
    public int BindingTrailMarker;
}

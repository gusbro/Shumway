namespace Shumway.Core;

/// <summary>
/// External mutable state that participates in backtracking (ADR-004's
/// reserved <see cref="TrailType.MutableSet"/> range). A builtin that mutates
/// host-level state (the global-variable store behind <c>b_setval/2</c> /
/// Scryer's <c>bb_b_put/2</c>) records the previous value with
/// <c>Activation.TrailExternal</c>; unwinding past the entry calls
/// <see cref="RestoreExternal"/> to put the old value back. A key is an atom
/// id: a mark of the activation claims it while the entry is on the trail
/// (ADR-003).
/// </summary>
public interface IExternalTrailTarget
{
    /// <summary>Restores <paramref name="key"/> to
    /// <paramref name="oldValue"/>, or removes it entirely when
    /// <paramref name="hadOldValue"/> is false (the trailed write created
    /// the entry).</summary>
    void RestoreExternal(int key, Cell oldValue, bool hadOldValue);
}

/// <summary>A store a compiled module writes backtrackably, through the host:
/// the module parks the write, and the host applies it when it takes the
/// parked writes (WasmAbi.GlobalWriteBase).</summary>
public interface IBacktrackableStore : IExternalTrailTarget
{
    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/> as a
    /// backtrackable write of activation <paramref name="ownerId"/>.</summary>
    void SetBacktrackable(int key, Cell value, int ownerId);
}

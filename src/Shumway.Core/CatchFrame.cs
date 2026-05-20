namespace Shumway.Core;

/// <summary>
/// One <c>catch/3</c> scope on the engine's catch-frame stack. While
/// <see cref="Active"/> the frame can intercept a thrown ball whose term
/// unifies with <see cref="Catcher"/>; the snapshot fields capture the
/// machine state to roll back to, so a caught throw undoes everything the
/// guarded goal did before the recovery goal runs.
///
/// <para>The frame is pushed by <c>'$catch_begin'</c> and deactivated by
/// <c>'$catch_end'</c>. Both operations are recorded on the extra trail
/// (<see cref="TrailType.CatchFrame"/>), so backtracking past a catch/3 —
/// or back into its guarded goal — restores the frame stack exactly.</para>
/// </summary>
public struct CatchFrame
{
    /// <summary>Heap index of the catcher term. The slot is allocated
    /// before the snapshot is taken, so it survives the heap truncation a
    /// caught throw performs.</summary>
    public int CatcherHeapIdx;

    /// <summary>Heap index of the recovery goal — a
    /// <c>'$catchrec_N'(Vars...)</c> helper call — executed in place of the
    /// rest of the catch on a catcher match.</summary>
    public int RecoveryHeapIdx;

    /// <summary>False once <c>'$catch_end'</c> has run: control has left
    /// the guarded goal, so a later throw must not be caught here.
    /// Backtracking into the guarded goal re-activates the frame.</summary>
    public bool Active;

    // ----- Machine snapshot taken at '$catch_begin' -----
    public int SnapB;
    public int SnapE;
    public int SnapHeapTop;
    public int SnapHb;
    public int SnapBindingTrailTop;
    public int SnapExtraTrailTop;

    // ----- Recovery continuation: where the enclosing clause resumes -----
    public int RecoveryE;
    public int RecoveryCp;
}

namespace Shumway.Core;

public sealed partial class Activation
{
    // ---------- ADR-049: the wake interrupt (the engine-side core) ----------
    //
    // Stage 1 put the interrupt in the interpreter's goal boundaries; stage 2
    // lets Tier-1 IL fire the same interrupt from its region boundaries, so
    // the core lives here where both tiers can reach it. The model: save the
    // interrupted goal's live state in an ordinary environment frame, load
    // the pending batch, and continue execution at the prelude's
    // '$wake_driver'/1 with CP set to the sentinel below. The driver runs
    // hooks and released goals as ordinary code; proceeding into the sentinel
    // restores the frame and resumes the interrupted point.

    /// <summary>The CP value that marks "the wake driver's caller". The
    /// dispatch loop's CP-jump sites consume it by restoring the wake frame
    /// instead of jumping. Negative (never a code address), distinct from the
    /// -1 query-top and the interpreter's -2 subroutine sentinel.</summary>
    public const int WakeReturnCp = -9;

    private static readonly int WakeDriverFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$wake_driver", permanent: true).Id, 1);
    private static readonly int WakeItemFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$wake", permanent: true).Id, 3);
    private static readonly int WakeLazyFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$wake_lazy", permanent: true).Id, 1);

    /// <summary>Arms the interrupt: builds the batch term, saves
    /// <paramref name="arity"/> argument registers plus B0,
    /// <paramref name="resumePc"/> and the arity itself in a fresh
    /// environment frame (Allocate stores E and CP), and points execution at
    /// the wake driver with CP = <see cref="WakeReturnCp"/> and a fresh cut
    /// barrier. Returns false when there is nothing to run (hookless queue —
    /// cleared) or nothing to run it WITH (no linked driver, unknowable
    /// arity) — the caller then keeps its pre-ADR-049 drain.</summary>
    public bool TryWakeInterrupt(int arity, int resumePc)
    {
        if (!HasAnyAttributeHook && !PendingWakeupsHaveLazy)
        {
            ClearPendingWakeups();
            return false;
        }
        var addrs = CurrentFunctorAddresses;
        if (arity < 0 || addrs is null
            || !addrs.TryGetValue(WakeDriverFunctorId, out int driverAddr))
            return false;

        var batch = TakePendingWakeups();
        for (int n = 0; n < batch.Count; n++)
            Profiler.Note("wakeup_hook_run");

        // The batch list, built back-to-front. Plain heap construction — no
        // meta-call runs before the driver takes over, so no GC inhibit:
        // once X0 holds the list every cell is reachable.
        Cell list = Cell.Atom(AtomTable.EmptyListId);
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            var (moduleId, attrValueIdx, otherIdx) = batch[i];
            Cell item;
            if (moduleId == LazyAttrModuleId)
            {
                int f = AllocateHeap(2);
                SetHeap(f, Cell.Functor(WakeLazyFunctorId));
                SetHeap(f + 1, Cell.Ref(attrValueIdx));
                item = Cell.Str(f);
            }
            else
            {
                int f = AllocateHeap(4);
                SetHeap(f, Cell.Functor(WakeItemFunctorId));
                SetHeap(f + 1, Cell.Atom(moduleId));
                SetHeap(f + 2, Cell.Ref(attrValueIdx));
                SetHeap(f + 3, Cell.Ref(otherIdx));
                item = Cell.Str(f);
            }
            int cons = AllocateHeap(2);
            SetHeap(cons, item);
            SetHeap(cons + 1, list);
            list = Cell.Lis(cons);
        }

        // The frame: Allocate() stores E and CP (the interrupted
        // continuation); the Y slots take the live registers and the three
        // control words. RawInt keeps the GC's Y-slot scan off them.
        Allocate(arity + 3);
        for (int i = 0; i < arity; i++)
            SetY(i, GetRegister(i));
        SetY(arity, Cell.RawInt(B0));
        SetY(arity + 1, Cell.RawInt(resumePc));
        SetY(arity + 2, Cell.RawInt(arity));

        SetCp(WakeReturnCp);
        SetB0(B);                 // the driver's own cut barrier
        SetRegister(0, list);
        SetPc(driverAddr);
        return true;
    }

    /// <summary>The driver proceeded into the sentinel: restore the
    /// interrupted goal's registers, barrier and continuation from the wake
    /// frame — the current environment; the driver's own frames are balanced
    /// — and resume. Deallocate does the E/CP restore and leaves the frame's
    /// memory alone while younger choice points protect it, which is exactly
    /// what a later re-entry through a wake alternative needs.
    ///
    /// <para>A resume point that is a forward resume marker (cursor 0) is a
    /// callee about to be (re-)entered: its cut barrier is B as of NOW —
    /// including any choice points the wake left, so a cut in the callee can
    /// never prune the wake's alternatives. Tier-0 gets the same for free by
    /// re-executing the call instruction, whose SetB0 runs post-wake.</para>
    /// </summary>
    public void WakeReturn()
    {
        int e = E;
        int n = (int)GetStack(e + EnvNOffset).Data;
        int arity = (int)GetY(e, n - 1).Data;
        for (int i = 0; i < arity; i++)
            SetRegister(i, GetY(e, i));
        SetB0((int)GetY(e, arity).Data);
        int resumePc = (int)GetY(e, arity + 1).Data;
        Deallocate();             // restores the interrupted E and CP
        if (IsResumeMarker(resumePc) && DecodeResumeMarker(resumePc).Cursor == 0)
            SetB0(B);
        SetPc(resumePc);
    }

    // ---------- the Tier-1 region-boundary entry points ----------
    // Verdicts: 0 = nothing to run (or the fallback drain ran and
    // succeeded) — continue; 1 = interrupt armed, P is at the driver and
    // IlTailCallPending is set — return true to the dispatch loop;
    // 2 = the fallback drain failed — branch to the fail label.

    /// <summary>The wake boundary in front of a region call site. CP must
    /// already hold the continuation the callee will proceed into (the
    /// caller's resume marker for a non-tail call; the region's inherited CP
    /// for a tail call): the wake frame captures it, and the resume is a
    /// forward marker that dispatches the callee.</summary>
    public int Tier1WakeBoundaryCall(int calleeFunctorId)
    {
        if (_pendingWakeups.Count == 0) return 0;
        (_, int arity) = FunctorTable.Lookup(calleeFunctorId);
        return WakeInterruptOrDrain(arity, EncodeResumeMarker(calleeFunctorId, 0));
    }

    /// <summary>The wake boundary at a region proceed (after any deallocate):
    /// no argument registers are live, and the resume simply jumps to the
    /// continuation CP already holds.</summary>
    public int Tier1WakeBoundaryProceed()
    {
        if (_pendingWakeups.Count == 0) return 0;
        return WakeInterruptOrDrain(0, Cp);
    }

    private int WakeInterruptOrDrain(int arity, int resumePc)
    {
        Profiler.Note("wakeup_flush");
        if (TryWakeInterrupt(arity, resumePc))
        {
            IlTailCallPending = true;   // "P is set; dispatch from it"
            return 1;
        }
        return Tier1WakeupFlusher is null || Tier1WakeupFlusher() ? 0 : 2;
    }
}

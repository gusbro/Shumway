using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The four ISO unification-comparison predicates. <c>=/2</c> and <c>\=/2</c>
/// drive unification (with rollback on the negation); <c>==/2</c> and
/// <c>\==/2</c> compare structures without binding anything.
/// </summary>
public static class UnifyBuiltins
{
    /// <summary><c>=(X, Y)</c> — succeeds iff X and Y can be unified, with any
    /// resulting bindings kept. Just delegates to the existing unify code.</summary>
    public static bool Unify(Activation engine) =>
        engine.UnifyRegisters(0, 1);

    /// <summary><c>unify_with_occurs_check(X, Y)</c> — ISO §8.2.2. Like
    /// <c>=/2</c> but every variable-to-compound binding is preceded
    /// by an occurs-check; binding a variable into a term where it
    /// already occurs (which plain <c>=/2</c> would resolve into a
    /// cyclic term) fails.</summary>
    public static bool UnifyWithOccursCheck(Activation engine) =>
        engine.UnifyRegistersWithOccursCheck(0, 1);

    /// <summary><c>'$not_unifiable3'(X, Y, -R)</c> — the native core of
    /// <c>\=/2</c> (a prelude wrapper). Performs a trial unification, unwinds
    /// any bindings the trial made, and reports three-state: <c>t</c> (cannot
    /// unify), <c>f</c> (unifies), or <c>m</c> — the trial bound an attributed
    /// variable, so the verdict is unreliable without running its hooks
    /// (freeze must fire, dif may veto): the wrapper re-decides via
    /// <c>\+ X = Y</c> in the live engine.</summary>
    public static bool NotUnifiable3(Activation engine)
    {
        int savedHeapTop = engine.HeapTop;
        int savedBindingTrail = engine.BindingTrailTop;
        int savedExtraTrail = engine.ExtraTrailTop;
        int savedHb = engine.Hb;
        int savedWakeups = engine.PendingWakeupCount;

        // Push Hb up to the current heap top so any binding made by the trial
        // unify is trailed — even bindings to "old" variables get trail
        // entries, which we'll need for the unwind.
        engine.SetHb(engine.HeapTop);

        bool unified = engine.UnifyRegisters(0, 1);
        bool wokeAttvars = engine.PendingWakeupCount != savedWakeups;

        // Unwind any bindings (whether the unify succeeded or partially
        // failed). Restore the heap top so trial-allocated cells are released,
        // and put Hb back to its original value. Wakeups the trial queued are
        // discarded WITH the bindings — the wrapper's meta path re-runs the
        // unification for real when they matter.
        engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
        engine.SetHeapTop(savedHeapTop);
        engine.SetHb(savedHb);
        engine.TruncatePendingWakeups(savedWakeups);

        string verdict = wokeAttvars ? "m" : unified ? "f" : "t";
        return engine.UnifyRegisterWithCell(
            2, Cell.Atom(AtomTable.Intern(verdict, permanent: true).Id));
    }

    /// <summary><c>==(X, Y)</c> — structural identity, no unification.</summary>
    public static bool StructurallyEqual(Activation engine) =>
        engine.AreStructurallyEqual(engine.GetRegister(0), engine.GetRegister(1));

    /// <summary><c>\==(X, Y)</c> — the negation of <c>==/2</c>.</summary>
    public static bool StructurallyNotEqual(Activation engine) =>
        !engine.AreStructurallyEqual(engine.GetRegister(0), engine.GetRegister(1));
}

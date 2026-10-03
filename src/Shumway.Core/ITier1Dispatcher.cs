namespace Shumway.Core;

/// <summary>
/// Hook the bytecode interpreter consults on every <c>call</c> /
/// <c>execute</c> dispatch to ask whether a Tier-1 IL replacement is
/// available for the target predicate. Returning <c>null</c> means
/// "no IL substitute — continue with bytecode dispatch"; returning a
/// function means "invoke this and skip the bytecode for this call,
/// success → continue at CP, failure → backtrack."
///
/// <para>This abstraction lets the interpreter (<c>Shumway.Interpreter</c>)
/// stay agnostic about the Tier-1 IL compiler (<c>Shumway.Compiler.Il</c>),
/// which is wired in by the embedding layer.</para>
/// </summary>
public interface ITier1Dispatcher
{
    /// <summary>Returns an IL replacement for the predicate located at
    /// <paramref name="targetAddress"/> in the running program, or
    /// <c>null</c> when there's no replacement yet (perhaps because the
    /// invocation counter hasn't reached the promotion threshold). The
    /// implementation may compile lazily on this call.</summary>
    Func<Activation, bool>? OnDispatch(int targetAddress);

    /// <summary>Threaded dispatch: returns the IL delegate
    /// already bound to the given functor id, or <c>null</c> if none
    /// is. The bytecode interpreter consults this when it lands on a
    /// resume-marker Pc (set by an IL non-tail Call site as the
    /// caller's continuation address) to re-enter the calling
    /// delegate at the forward-resume cursor without creating a
    /// recursive C# stack frame.</summary>
    Func<Activation, int, bool>? ResolveByFunctorId(int functorId);

    /// <summary>The linked address of the predicate with this functor id
    /// in the running program, or -1. The interpreter needs it when a
    /// <c>CallIl</c> site finds no delegate (evicted since the site was
    /// rewritten): the site goes back to a plain <c>Call</c>.</summary>
    int AddressOfFunctor(int functorId);

    /// <summary>Whether <paramref name="address"/> is the entry of a
    /// predicate in the running program (what <see cref="OnDispatch"/>
    /// accepts), as opposed to a continuation inside a clause.</summary>
    bool IsPredicateEntry(int address);

    /// <summary>A call from bytecode at <paramref name="sitePc"/> reached
    /// compiled code. Counts one invocation of the predicate that contains the
    /// site: one called rarely but calling promoted code in a loop (a
    /// generate-and-test driver) promotes too, where counting its own calls
    /// alone never gets it there.</summary>
    void CreditCaller(int sitePc);

    /// <summary>The delegate a functor had before it was evicted, for a
    /// resume only (a cursor past the entry): a choice point or a
    /// continuation left in a call that began before the eviction. That
    /// call finishes on the code it began with, which is the logical update
    /// view when the eviction was a mutation (ADR-054). Never for a fresh
    /// call, which must reach the predicate as it is now. Null when the
    /// functor never had one.</summary>
    Func<Activation, int, bool>? ResolveRetiredResume(int functorId);

    /// <summary>ADR-049 point 11: compiled code wakes in front of every goal
    /// that is not a unification (continuation methods, ADR-061), so a return
    /// into it continues the stretch of unifications. False where compiled
    /// code wakes at its own returns instead (regions, the wasm tier).</summary>
    bool CompiledCodeWakes { get; }
}

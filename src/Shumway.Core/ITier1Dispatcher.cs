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
}

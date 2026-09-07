namespace Shumway.Core;

/// <summary>The backstop for the walks over user data that are still
/// recursive. A .NET stack overflow cannot be caught: it kills the process,
/// with no goal to unwind and nothing to report — so a term nested deeper
/// than the C# stack can hold has to be refused BEFORE the stack runs out.
///
/// <para>Deep nesting that the engine can handle iteratively is handled
/// iteratively (a clause body's conjunction spine, a list's elements); this
/// covers what is left — a term nested through operators or parentheses, and
/// the pipeline transforms whose recursion is structural rather than a walk.
/// The check probes for room to run an average .NET frame, so it fires with
/// stack to spare, and the refusal reaches the program as an ordinary
/// catchable ball.</para></summary>
public static class RecursionGuard
{
    /// <summary>Raises <c>resource_error(term_nesting)</c> when the C# stack
    /// is close to exhausted. Call at the top of a recursion whose depth a
    /// program's data decides.</summary>
    public static void EnsureRoom()
    {
#if NETFRAMEWORK
        // .NET Framework has only the throwing probe (the Try form arrived
        // with .NET Core); an untaken try block costs nothing to enter.
        try { System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); }
        catch (InsufficientExecutionStackException)
        {
            throw new PrologRuntimeException("resource_error", "term_nesting");
        }
#else
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new PrologRuntimeException("resource_error", "term_nesting");
#endif
    }
}

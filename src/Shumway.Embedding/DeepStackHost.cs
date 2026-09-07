namespace Shumway.Embedding;

/// <summary>Runs a host's work on a thread with room for deep recursion.
///
/// <para>Reading a term and transforming a clause descend the term the source
/// wrote: nesting through parentheses and operators, and the pipeline stages
/// whose recursion is structural. A .NET thread's default stack is a megabyte,
/// which is a few hundred levels of that — small enough for real generated
/// code to reach. <see cref="Shumway.Core.RecursionGuard"/> keeps the ceiling
/// from being fatal (a catchable <c>resource_error(term_nesting)</c> instead
/// of a dead process); this puts the ceiling where nothing real reaches it.
///
/// <para>For our own command-line tools, whose whole job is loading programs.
/// An embedding host chooses its own threads, and gets the guard's refusal on
/// whatever stack it runs on — which is the point of the guard: a library must
/// never take the application down.</para></summary>
public static class DeepStackHost
{
    /// <summary>The stack a tool's main thread gets. 64 MB of ADDRESS space —
    /// reserved, not committed, so the pages a run never touches cost
    /// nothing.</summary>
    public const int DefaultStackBytes = 64 * 1024 * 1024;

    /// <summary>Runs <paramref name="main"/> on a thread with
    /// <paramref name="stackBytes"/> of stack and returns its exit code.
    /// Exceptions propagate to the caller as if it had run inline.</summary>
    public static int Run(Func<int> main, int stackBytes = DefaultStackBytes)
    {
        ArgumentNullException.ThrowIfNull(main);
        int result = 0;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { result = main(); }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }, stackBytes);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}

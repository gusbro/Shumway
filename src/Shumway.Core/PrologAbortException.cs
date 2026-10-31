namespace Shumway.Core;

/// <summary>
/// Raised by <c>abort/0</c> to abandon the running query. It is not a Prolog
/// ball: <c>catch/3</c> never intercepts it, while the cleanups of the
/// <c>setup_call_cleanup/3</c> scopes it leaves still run. It unwinds every
/// nested query up to the outermost one, whose caller receives it: the top
/// level goes back to its prompt, an executable exits with status 1, an
/// embedding host sees it thrown from <c>Query</c> / <c>QueryAll</c>.
/// </summary>
public sealed class PrologAbortException : Exception
{
    public PrologAbortException()
        : base("Prolog execution aborted.")
    {
    }
}

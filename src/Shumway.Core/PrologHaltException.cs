namespace Shumway.Core;

/// <summary>
/// Raised by the <c>halt/0</c> and <c>halt/1</c> built-ins to abort
/// the engine cleanly. The embedding caller catches this at the
/// outermost <c>Query</c> / <c>QueryAll</c> boundary and exits the
/// iteration, optionally surfacing the exit code to the host.
///
/// <para>Unlike <see cref="PrologRuntimeException"/> this never
/// translates into an ISO <c>error/2</c> term — a <c>halt</c> is a
/// terminating action, not a recoverable error, and <c>catch/3</c>
/// does NOT intercept it.</para>
/// </summary>
public sealed class PrologHaltException : Exception
{
    /// <summary>Exit code requested by the caller of <c>halt/1</c>. The
    /// parameterless <c>halt/0</c> defaults to zero.</summary>
    public int ExitCode { get; }

    public PrologHaltException(int exitCode)
        : base($"Prolog halt requested (exit code {exitCode}).")
    {
        ExitCode = exitCode;
    }
}

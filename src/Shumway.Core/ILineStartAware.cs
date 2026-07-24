namespace Shumway.Core;

/// <summary>
/// A <see cref="System.IO.TextWriter"/> that knows whether its next character
/// would land at column 0 — i.e. the last thing written was a newline (or nothing
/// has been written yet). Output that wants to begin on a fresh line only when it
/// is not already there — the <c>time/1</c> resource report, the top-level's
/// answer line — queries this instead of a hardware cursor, so it is correct under
/// redirection too.
/// </summary>
public interface ILineStartAware
{
    /// <summary>True when the next character would be written at column 0.</summary>
    bool AtLineStart { get; }
}

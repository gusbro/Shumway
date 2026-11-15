using System.Runtime.InteropServices.JavaScript;

namespace Shumway.Web;

/// <summary>
/// What <c>read/1</c> reads. A browser has no standard input, so
/// <c>user_input</c> would otherwise be end-of-file from the start — a program
/// whose goal is <c>read(X)</c> would answer <c>X = end_of_file</c> without ever
/// asking, which is not what it does anywhere else.
///
/// <para>The engine's read is synchronous: it blocks the thread until it has
/// characters. That is exactly why the search runs on a pool thread — blocking
/// there costs the page nothing, and the prompt is drawn by the UI thread while
/// the engine waits. The request goes out through the runtime thread (JavaScript
/// interop is thread-affine), and the answer comes back through
/// <see cref="SupplyInput"/>, which must not queue behind the engine gate: the
/// engine is holding it, blocked, waiting for precisely this.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    /// <summary>Asks the page to collect a line for the running program.</summary>
    [JSImport("ui.askForInput", "main.js")]
    internal static partial void AskForInput();

    /// <summary>Hands the running program a line of input (a newline is added —
    /// the program is reading lines, not keystrokes).</summary>
    [JSExport]
    internal static Task SupplyInput(string text)
    {
        S.Input.Supply(text);
        return Task.CompletedTask;
    }

    /// <summary>Ends the input stream: <c>read/1</c> answers
    /// <c>end_of_file</c>. Also how a program stuck on input is let go.</summary>
    [JSExport]
    internal static Task SupplyEndOfFile()
    {
        S.Input.SupplyEndOfFile();
        return Task.CompletedTask;
    }
}

using System.Runtime.InteropServices.JavaScript;

namespace Shumway.Web;

/// <summary>
/// What <c>read/1</c> reads. A browser has no standard input, so
/// <c>user_input</c> would otherwise be end-of-file from the start — a program
/// whose goal is <c>read(X)</c> would answer <c>X = end_of_file</c> without ever
/// asking, which is not what it does anywhere else.
///
/// <para>The engine's read is synchronous: it blocks the thread until it has
/// characters. That is exactly why the search runs on a POOL thread — blocking
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

    private static readonly PageReader _pageInput = new();

    /// <summary>Hands the running program a line of input (a newline is added —
    /// the program is reading lines, not keystrokes).</summary>
    [JSExport]
    internal static Task SupplyInput(string text)
    {
        _pageInput.Supply(text);
        return Task.CompletedTask;
    }

    /// <summary>Ends the input stream: <c>read/1</c> answers
    /// <c>end_of_file</c>. Also how a program stuck on input is let go.</summary>
    [JSExport]
    internal static Task SupplyEndOfFile()
    {
        _pageInput.SupplyEof();
        return Task.CompletedTask;
    }

    private sealed class PageReader : TextReader
    {
        private readonly object _lock = new();
        private string _buffer = "";
        private int _at;
        private bool _eof;

        public override int Peek() => Available() ? _buffer[_at] : -1;

        public override int Read() => Available() ? _buffer[_at++] : -1;

        /// <summary>True once there is a character to hand out; asks the page and
        /// BLOCKS if there is not. False only at end of file.</summary>
        private bool Available()
        {
            lock (_lock)
            {
                while (true)
                {
                    if (_at < _buffer.Length) return true;
                    if (_eof) return false;
                    if (_jsThread is null) return false;   // no page to ask
                    // Blocking is only safe OFF the runtime thread — that thread
                    // has to be free to deliver the answer. It never reads (the
                    // search is on a pool thread), but if one ever did, an
                    // immediate end-of-file beats a deadlocked tab.
                    if (SynchronizationContext.Current == _jsThread) return false;
                    // Ask on the thread that owns the JavaScript side, then wait.
                    // Monitor.Wait releases the lock, so the answer can arrive.
                    _jsThread.Post(static _ => AskForInput(), null);
                    // CA1416: blocking is unsupported on the browser's MAIN
                    // thread; the guard above is what keeps this off it.
#pragma warning disable CA1416
                    Monitor.Wait(_lock);
#pragma warning restore CA1416
                }
            }
        }

        public void Supply(string text)
        {
            lock (_lock)
            {
                // Keep only what has not been read: a program reading term by
                // term leaves the rest of the line for the next read.
                _buffer = _buffer[_at..] + text + "\n";
                _at = 0;
                Monitor.PulseAll(_lock);
            }
        }

        public void SupplyEof()
        {
            lock (_lock) { _eof = true; Monitor.PulseAll(_lock); }
        }

        /// <summary>Reopens the stream for the next query — an end-of-file
        /// answered once should not make every later read/1 return it.</summary>
        public void Reset()
        {
            lock (_lock) { _buffer = ""; _at = 0; _eof = false; Monitor.PulseAll(_lock); }
        }
    }
}

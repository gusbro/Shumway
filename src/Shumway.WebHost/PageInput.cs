using Shumway.Core;

namespace Shumway.WebHost;

/// <summary>What <c>read/1</c> reads in a session: <c>user_input</c> when
/// there is no standard input to read.
///
/// <para>The engine's read is synchronous: it blocks its thread until there
/// are characters. The request goes to the page through the page thread, and
/// the answer comes back through <see cref="Supply"/>, which must not queue
/// behind the engine: the engine is blocked waiting for exactly that.</para>
///
/// <para>An end of file the user gives is a terminal's Ctrl-D: it ends the
/// read that gets it, and the program reading past it is asked again
/// (<see cref="IReopenableInput"/>). <see cref="EndInput"/> ends the input until
/// the next query.</para></summary>
public sealed class PageInput : TextReader, IReopenableInput
{
    private readonly SynchronizationContext? _pageThread;
    private readonly ISessionPage _page;
    private readonly object _lock = new();
    private string _buffer = "";
    private int _at;
    private bool _eof;
    private bool _closed;

    public PageInput(SynchronizationContext? pageThread, ISessionPage page)
    {
        _pageThread = pageThread;
        _page = page ?? throw new ArgumentNullException(nameof(page));
    }

    public override int Peek() => Available() ? _buffer[_at] : -1;

    public override int Read() => Available() ? _buffer[_at++] : -1;

    /// <summary>True once there is a character to hand out; asks the page and
    /// blocks if there is not. False only at end of file.</summary>
    private bool Available()
    {
        lock (_lock)
        {
            while (true)
            {
                if (_at < _buffer.Length) return true;
                if (_eof || _closed) return false;
                if (_pageThread is null)
                {
                    // No page thread: the page is asked from here, and its
                    // answer arrives from whatever thread it likes.
                    _page.AskForInput();
                }
                else
                {
                    // The page thread has to stay free to deliver the answer.
                    // It never reads, but if it did, end of file beats a
                    // deadlocked tab.
                    if (SynchronizationContext.Current == _pageThread) return false;
                    _pageThread.Post(static o => ((ISessionPage)o!).AskForInput(), _page);
                }
                if (_at < _buffer.Length) return true;
                if (_eof || _closed) return false;
                // Wait releases the lock, so the answer can arrive.
                Monitor.Wait(_lock);
            }
        }
    }

    /// <summary>Hands the running program a line. A newline is added: the
    /// program reads lines, not keystrokes.</summary>
    public void Supply(string text)
    {
        lock (_lock)
        {
            // Keep what has not been read: a program reading term by term
            // leaves the rest of the line for the next read.
            _buffer = _buffer[_at..] + text + "\n";
            _at = 0;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>The user ends the input: the read waiting answers
    /// <c>end_of_file</c>, and one after it asks again.</summary>
    public void SupplyEndOfFile()
    {
        lock (_lock) { _eof = true; Monitor.PulseAll(_lock); }
    }

    /// <summary>The program reads past the end of file the user gave.</summary>
    public void Reopen()
    {
        lock (_lock) _eof = false;
    }

    /// <summary>Ends the input until <see cref="Reset"/>: every read answers
    /// <c>end_of_file</c>. How a goal that is stopped, or whose engine is
    /// going, is let go of when it waits for input.</summary>
    public void EndInput()
    {
        lock (_lock) { _closed = true; Monitor.PulseAll(_lock); }
    }

    /// <summary>Reopens the stream for the next query: an end of file answered
    /// once must not be what every later read gets.</summary>
    public void Reset()
    {
        lock (_lock) { _buffer = ""; _at = 0; _eof = false; _closed = false; Monitor.PulseAll(_lock); }
    }
}

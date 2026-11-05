using System.Text;

namespace Shumway.Repl;

/// <summary>The console's keys while a query runs. The watcher has to read a key
/// to tell whether it is ESC, and that takes it off the console. A key that is
/// not for the watcher waits here for the goal's next read, so a program that
/// reads the keyboard can be typed at while it works.</summary>
internal sealed class QueryKeyboard
{
    /// <summary>A key held down during a long computation must not grow the
    /// queue without limit: past this, keys are dropped.</summary>
    internal const int MaxKeysAhead = 4096;

    private readonly object _gate = new();
    private readonly Queue<ConsoleKeyInfo> _ahead = new();
    private readonly Func<bool> _keyAvailable;
    private readonly Func<ConsoleKeyInfo> _readKey;
    private bool _goalReading;

    public QueryKeyboard(Func<bool> keyAvailable, Func<ConsoleKeyInfo> readKey)
    {
        _keyAvailable = keyAvailable;
        _readKey = readKey;
    }

    /// <summary>For the watcher: takes the keys pressed so far and keeps them for
    /// the goal, up to the first one the watcher has to act on, which it returns:
    /// ESC, or Ctrl+C delivered as a key. Takes nothing while a read of the goal
    /// waits on the console, since those keys are the goal's, ESC included.</summary>
    public ConsoleKeyInfo? Watch()
    {
        lock (_gate)
        {
            while (!_goalReading && _keyAvailable())
            {
                ConsoleKeyInfo key = _readKey();
                if (IsForTheWatcher(key)) return key;
                if (_ahead.Count < MaxKeysAhead) _ahead.Enqueue(key);
            }
            return null;
        }
    }

    private static bool IsForTheWatcher(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Escape
        || (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0);

    /// <summary>The next key for the goal: one pressed earlier, or else the next
    /// one of the console.</summary>
    public ConsoleKeyInfo NextKey()
    {
        lock (_gate)
        {
            if (_ahead.Count > 0) return _ahead.Dequeue();
            _goalReading = true;
        }
        try { return _readKey(); }
        finally { EndRead(); }
    }

    /// <summary>Starts a read of a line from the console by the goal, until
    /// <see cref="EndRead"/>. Gives the text typed ahead for that line, with
    /// Backspace applied. True when Enter ended the line: the keys after it stay
    /// for the next read.</summary>
    public bool BeginLineRead(out string typed)
    {
        var text = new StringBuilder();
        lock (_gate)
        {
            _goalReading = true;
            while (_ahead.Count > 0)
            {
                ConsoleKeyInfo key = _ahead.Dequeue();
                char c = key.KeyChar;
                if (key.Key == ConsoleKey.Enter || c == '\r' || c == '\n')
                {
                    typed = text.ToString();
                    return true;
                }
                if (key.Key == ConsoleKey.Backspace || c == '\b')
                {
                    if (text.Length > 0) text.Length--;
                }
                else if (c == '\t' || !char.IsControl(c))
                {
                    text.Append(c);
                }
            }
        }
        typed = text.ToString();
        return false;
    }

    /// <summary>Ends a read of the goal: the watcher takes keys again.</summary>
    public void EndRead()
    {
        lock (_gate) _goalReading = false;
    }

    /// <summary>Drops the keys no read took.</summary>
    public void Clear()
    {
        lock (_gate) _ahead.Clear();
    }
}

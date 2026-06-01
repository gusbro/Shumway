using System.Text;

namespace Shumway.Repl;

/// <summary>
/// Chunk 249 — interactive line editor for the REPL. Built on
/// <see cref="Console.ReadKey"/> so it works cross-platform without
/// pulling in a heavyweight terminal library. Supports the
/// expected baseline:
///
/// <list type="bullet">
/// <item><c>←</c> / <c>→</c> — move cursor one character.</item>
/// <item><c>Home</c> / <c>Ctrl-A</c>, <c>End</c> / <c>Ctrl-E</c>
///   — jump to line start / end.</item>
/// <item><c>Backspace</c>, <c>Delete</c> — edit at the cursor.</item>
/// <item><c>↑</c> / <c>↓</c> — navigate the persistent history.
///   The in-progress draft is preserved when stepping back into it.</item>
/// <item><c>Enter</c> — commit the line.</item>
/// <item><c>Ctrl-D</c> (EOF) — returns <c>null</c> at an empty
///   line, signalling end of input the same way
///   <see cref="Console.ReadLine"/> would.</item>
/// </list>
///
/// <para>When standard input is redirected (a test harness, a
/// pipe, a script) the editor degrades to plain
/// <see cref="Console.ReadLine"/> so the REPL stays scriptable
/// — keystroke handling depends on a real terminal.</para>
///
/// <para>Line wrap: the editor assumes the line fits in the
/// terminal width. Long input that wraps still works
/// semantically, but visual feedback may be off until the next
/// redraw. Improving this needs explicit cursor row tracking;
/// out of scope for v1.</para>
/// </summary>
internal sealed class LineEditor
{
    private readonly HistoryStore _history;

    public LineEditor(HistoryStore history)
    {
        _history = history;
    }

    /// <summary>Reads one line of input with editing support.
    /// Returns the entered text (which may be empty), or
    /// <c>null</c> at end of input (Ctrl-D on Unix, Ctrl-Z+Enter
    /// on Windows, or stdin closed). Adds the line to history if
    /// non-empty.</summary>
    public string? ReadLine(string prompt)
    {
        // Scripted / redirected input: fall back to plain
        // ReadLine. Keystroke handling needs an interactive
        // terminal, and the editor's redraw logic would corrupt
        // piped output.
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            string? raw = Console.ReadLine();
            if (raw is not null) _history.Add(raw);
            return raw;
        }

        Console.Write(prompt);
        var buffer = new StringBuilder();
        int cursor = 0;
        int historyIndex = _history.Entries.Count;  // points past the end
        string draft = "";                          // in-progress line saved when we step into history

        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException)
            {
                // Some hosts mark IsInputRedirected as false but
                // still don't have an interactive console; ReadKey
                // throws there. Bail to plain ReadLine for the
                // rest of this line.
                Console.Write(buffer.ToString());
                string? rest = Console.ReadLine();
                string composed = buffer.ToString() + (rest ?? "");
                if (composed.Length > 0) _history.Add(composed);
                return rest is null ? null : composed;
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    string line = buffer.ToString();
                    _history.Add(line);
                    return line;

                case ConsoleKey.Backspace:
                    if (cursor > 0)
                    {
                        buffer.Remove(cursor - 1, 1);
                        cursor--;
                        Redraw(prompt, buffer, cursor);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursor < buffer.Length)
                    {
                        buffer.Remove(cursor, 1);
                        Redraw(prompt, buffer, cursor);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursor > 0) { cursor--; SetCursor(prompt, cursor); }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursor < buffer.Length) { cursor++; SetCursor(prompt, cursor); }
                    break;

                case ConsoleKey.Home:
                    cursor = 0; SetCursor(prompt, cursor);
                    break;

                case ConsoleKey.End:
                    cursor = buffer.Length; SetCursor(prompt, cursor);
                    break;

                case ConsoleKey.UpArrow:
                    if (historyIndex > 0)
                    {
                        // Stepping out of the in-progress line for
                        // the first time — save it so Down can come
                        // back.
                        if (historyIndex == _history.Entries.Count)
                            draft = buffer.ToString();
                        historyIndex--;
                        buffer.Clear();
                        buffer.Append(_history.Entries[historyIndex]);
                        cursor = buffer.Length;
                        Redraw(prompt, buffer, cursor);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (historyIndex < _history.Entries.Count)
                    {
                        historyIndex++;
                        buffer.Clear();
                        buffer.Append(historyIndex == _history.Entries.Count
                            ? draft
                            : _history.Entries[historyIndex]);
                        cursor = buffer.Length;
                        Redraw(prompt, buffer, cursor);
                    }
                    break;

                default:
                    // Ctrl-D at an empty line → EOF (like
                    // ReadLine returning null).
                    if (key.Key == ConsoleKey.D
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        if (buffer.Length == 0)
                        {
                            Console.WriteLine();
                            return null;
                        }
                        break;
                    }
                    // Ctrl-A / Ctrl-E — Emacs-style line jumps.
                    if (key.Key == ConsoleKey.A
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    { cursor = 0; SetCursor(prompt, cursor); break; }
                    if (key.Key == ConsoleKey.E
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    { cursor = buffer.Length; SetCursor(prompt, cursor); break; }
                    // Ctrl-U — kill to start of line (Emacs / readline standard).
                    if (key.Key == ConsoleKey.U
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        buffer.Remove(0, cursor);
                        cursor = 0;
                        Redraw(prompt, buffer, cursor);
                        break;
                    }
                    // Ctrl-K — kill to end of line.
                    if (key.Key == ConsoleKey.K
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        buffer.Remove(cursor, buffer.Length - cursor);
                        Redraw(prompt, buffer, cursor);
                        break;
                    }
                    // Plain printable character (includes non-ASCII
                    // letters). Insert at cursor.
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(cursor, key.KeyChar);
                        cursor++;
                        Redraw(prompt, buffer, cursor);
                    }
                    break;
            }
        }
    }

    /// <summary>Repaints the line in place: cursor back to the start
    /// of the prompt, write prompt + buffer, trailing space to erase
    /// any character left over from a shrunken line, then snap the
    /// cursor back to its logical position.</summary>
    private static void Redraw(string prompt, StringBuilder buffer, int cursor)
    {
        // Save the row in case the line wraps — the prompt's column
        // is always 0 so CursorLeft = 0 only handles the unwrapped
        // case. We assume unwrapped; long lines display fine, the
        // cursor just lands wrong on the rewrap.
        try { Console.CursorLeft = 0; }
        catch (System.IO.IOException) { /* not interactive */ return; }
        Console.Write(prompt);
        Console.Write(buffer.ToString());
        // One trailing space wipes the character that was here
        // before a Backspace / Delete shrank the buffer. Costs one
        // visible character of horizontal space, harmless.
        Console.Write(' ');
        SetCursor(prompt, cursor);
    }

    private static void SetCursor(string prompt, int cursor)
    {
        try { Console.CursorLeft = prompt.Length + cursor; }
        catch (System.IO.IOException) { /* not interactive */ }
        catch (ArgumentOutOfRangeException) { /* cursor past edge */ }
    }
}

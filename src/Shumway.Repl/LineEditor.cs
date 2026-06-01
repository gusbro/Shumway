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
public sealed class LineEditor
{
    private readonly HistoryStore _history;
    private readonly Func<string, IReadOnlyList<string>>? _completer;

    public LineEditor(HistoryStore history,
        Func<string, IReadOnlyList<string>>? completer = null)
    {
        _history = history;
        _completer = completer;
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
                    // Chunk 253: always Redraw on cursor moves so
                    // the horizontal-scroll window slides correctly
                    // when the cursor crosses the visible boundary.
                    if (cursor > 0) { cursor--; Redraw(prompt, buffer, cursor); }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursor < buffer.Length) { cursor++; Redraw(prompt, buffer, cursor); }
                    break;

                case ConsoleKey.Home:
                    cursor = 0; Redraw(prompt, buffer, cursor);
                    break;

                case ConsoleKey.End:
                    cursor = buffer.Length; Redraw(prompt, buffer, cursor);
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

                case ConsoleKey.Tab:
                    // Chunk 250 — completion. Identify the
                    // identifier-word at the cursor, ask the
                    // completer for matching atoms. Skip when no
                    // completer is wired or no word is at hand.
                    if (_completer is not null)
                    {
                        int wordStart = FindWordStart(buffer, cursor);
                        int wordLen = cursor - wordStart;
                        if (wordLen > 0)
                        {
                            string prefix = buffer.ToString(wordStart, wordLen);
                            var candidates = _completer(prefix);
                            if (candidates.Count == 1)
                            {
                                string completion = candidates[0];
                                buffer.Remove(wordStart, wordLen);
                                buffer.Insert(wordStart, completion);
                                cursor = wordStart + completion.Length;
                                Redraw(prompt, buffer, cursor);
                            }
                            else if (candidates.Count > 1)
                            {
                                // Common-prefix completion: extend the
                                // word as far as every candidate agrees,
                                // then list the alternatives below.
                                string common = LongestCommonPrefix(candidates);
                                if (common.Length > prefix.Length)
                                {
                                    buffer.Remove(wordStart, wordLen);
                                    buffer.Insert(wordStart, common);
                                    cursor = wordStart + common.Length;
                                }
                                Console.WriteLine();
                                PrintCandidates(candidates);
                                // The candidate list pushed the
                                // prompt up; Redraw re-emits it +
                                // the buffer + cursor at the
                                // fresh row's column 0.
                                Redraw(prompt, buffer, cursor);
                            }
                            // No matches → silent (no annoying bell).
                        }
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
                    { cursor = 0; Redraw(prompt, buffer, cursor); break; }
                    if (key.Key == ConsoleKey.E
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    { cursor = buffer.Length; Redraw(prompt, buffer, cursor); break; }
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

    /// <summary>Chunk 253 — repaints the line using a horizontal-
    /// scroll window over the buffer. The cursor stays on a single
    /// terminal row regardless of buffer length: when the buffer
    /// would overflow the row, the visible window slides so the
    /// cursor remains in view.
    ///
    /// <para>Why scroll rather than wrap-and-redraw: the previous
    /// implementation wrote the whole buffer on every keystroke
    /// and reset only <see cref="Console.CursorLeft"/> (the x
    /// coordinate) before the next paint. For a buffer that
    /// wrapped to a second terminal row, the reset landed on the
    /// last visible row only — the wrapped-up portion stayed
    /// painted, and the new render piled on top, leaving stale
    /// fragments of prior typing across multiple rows.</para>
    ///
    /// <para>Trade-off: the user can't see the start of a long
    /// line while editing the end. Acceptable for an interactive
    /// REPL where deeply-edited inputs are rare; the alternative
    /// (multi-row tracking with ANSI clear-to-end-of-screen) is
    /// heavier and still has terminal-portability issues.</para></summary>
    private static void Redraw(string prompt, StringBuilder buffer, int cursor)
    {
        int width = TerminalWidthOrDefault();
        int visibleCols = Math.Max(1, width - prompt.Length - 1);
        var (visStart, visEnd) = ComputeVisibleWindow(
            bufferLength: buffer.Length, cursor: cursor, visibleCols: visibleCols);

        try { Console.CursorLeft = 0; }
        catch (System.IO.IOException) { return; }
        Console.Write(prompt);
        if (visEnd > visStart)
            Console.Write(buffer.ToString(visStart, visEnd - visStart));

        // Pad with spaces to overwrite any leftover from a longer
        // previous render. Width-1 keeps the cursor inside the row
        // so writing the last column doesn't force a wrap.
        int painted = prompt.Length + (visEnd - visStart);
        int padding = Math.Max(0, width - 1 - painted);
        if (padding > 0) Console.Write(new string(' ', padding));

        SetCursor(prompt.Length + (cursor - visStart));
    }

    private static void SetCursor(int column)
    {
        try { Console.CursorLeft = column; }
        catch (System.IO.IOException) { /* not interactive */ }
        catch (ArgumentOutOfRangeException) { /* cursor past edge */ }
    }

    private static int TerminalWidthOrDefault()
    {
        try
        {
            int w = Console.WindowWidth;
            return w < 20 ? 80 : w;
        }
        catch { return 80; }
    }

    /// <summary>Chunk 253 — pure helper computing the
    /// horizontal-scroll window over the buffer. Returns
    /// <c>[visStart, visEnd)</c> such that <c>cursor</c> is
    /// inside it and the window fits in
    /// <paramref name="visibleCols"/> columns.
    ///
    /// <para>Rules:</para>
    /// <list type="bullet">
    /// <item>Buffer that fits → window covers all of it.</item>
    /// <item>Buffer longer than the window but cursor near the
    ///   start → anchor window at 0.</item>
    /// <item>Otherwise → end window one cell past cursor (so the
    ///   cursor is the last visible column) and back-fill
    ///   visibleCols chars to the left.</item>
    /// </list></summary>
    public static (int Start, int End) ComputeVisibleWindow(
        int bufferLength, int cursor, int visibleCols)
    {
        if (bufferLength <= visibleCols)
            return (0, bufferLength);
        if (cursor < visibleCols)
            return (0, visibleCols);
        int end = Math.Min(bufferLength, cursor + 1);
        int start = Math.Max(0, end - visibleCols);
        return (start, end);
    }

    /// <summary>Chunk 250 — walks back from <paramref name="cursor"/>
    /// while the character is identifier-class (alnum / underscore),
    /// returning the start position of the word. Identifier-class
    /// matches Prolog's atom-token shape; everything else (paren,
    /// operator, whitespace) is treated as a word boundary so a
    /// completion against e.g. "asse" doesn't try to also pull in
    /// the preceding `(`.</summary>
    public static int FindWordStart(StringBuilder buffer, int cursor)
    {
        int i = cursor;
        while (i > 0 && IsIdentChar(buffer[i - 1])) i--;
        return i;
    }

    private static bool IsIdentChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9') || c == '_';

    /// <summary>Longest string that's a prefix of every entry in
    /// <paramref name="candidates"/>. Used to extend the word as
    /// far as every match agrees before falling back to the "list
    /// alternatives" UI.</summary>
    public static string LongestCommonPrefix(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return "";
        string first = candidates[0];
        int max = first.Length;
        for (int k = 1; k < candidates.Count; k++)
        {
            string s = candidates[k];
            int j = 0;
            while (j < max && j < s.Length && s[j] == first[j]) j++;
            max = j;
            if (max == 0) break;
        }
        return first.Substring(0, max);
    }

    /// <summary>Prints the alternatives multi-column to fit the
    /// terminal width. Falls back to one per line if the width
    /// is unknown.</summary>
    private static void PrintCandidates(IReadOnlyList<string> candidates)
    {
        int width;
        try { width = Console.WindowWidth; }
        catch { width = 80; }
        if (width < 20) width = 80;

        int maxLen = 0;
        foreach (var c in candidates)
            if (c.Length > maxLen) maxLen = c.Length;
        int colWidth = maxLen + 2;
        int cols = Math.Max(1, width / colWidth);

        int col = 0;
        foreach (var c in candidates)
        {
            Console.Write(c.PadRight(colWidth));
            col++;
            if (col >= cols) { Console.WriteLine(); col = 0; }
        }
        if (col != 0) Console.WriteLine();
    }
}

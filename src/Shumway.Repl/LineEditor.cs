using System.Text;

namespace Shumway.Repl;

/// <summary>
/// Interactive line editor for the REPL. Built on
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
/// <para>Line wrap (Phase 31): input wider than the terminal wraps
/// onto further rows like a normal shell. <see cref="LineView"/>
/// repaints the whole <c>prompt + buffer</c> from a captured origin
/// row on every edit, letting the console wrap naturally, then
/// positions the hardware cursor at the logical edit position —
/// across rows. It detects terminal scroll (when the painted line
/// reaches the bottom of the window and pushes the origin up) and
/// shifts the origin so cursor placement stays aligned. This
/// replaces the chunk-253 horizontal-scroll window, which kept the
/// line on a single row and hid its start while editing the end.</para>
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

        // Capture the origin row *before* writing the prompt so the
        // view can repaint from the line's true start on every edit.
        int originRow;
        try { originRow = Console.CursorTop; }
        catch { originRow = 0; }
        Console.Write(prompt);
        var view = new LineView(prompt, originRow);

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
                    // Drop below the whole (possibly multi-row) line
                    // before the newline so following output starts
                    // clean.
                    view.MoveToEnd(buffer.Length);
                    Console.WriteLine();
                    string line = buffer.ToString();
                    _history.Add(line);
                    return line;

                case ConsoleKey.Backspace:
                    if (cursor > 0)
                    {
                        buffer.Remove(cursor - 1, 1);
                        cursor--;
                        view.Render(buffer, cursor);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursor < buffer.Length)
                    {
                        buffer.Remove(cursor, 1);
                        view.Render(buffer, cursor);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursor > 0) { cursor--; view.Render(buffer, cursor); }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursor < buffer.Length) { cursor++; view.Render(buffer, cursor); }
                    break;

                case ConsoleKey.Home:
                    cursor = 0; view.Render(buffer, cursor);
                    break;

                case ConsoleKey.End:
                    cursor = buffer.Length; view.Render(buffer, cursor);
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
                        view.Render(buffer, cursor);
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
                        view.Render(buffer, cursor);
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
                                view.Render(buffer, cursor);
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
                                // Move below the line, list candidates,
                                // then re-anchor the view at the fresh
                                // row and repaint prompt + buffer there.
                                view.MoveToEnd(buffer.Length);
                                Console.WriteLine();
                                PrintCandidates(candidates);
                                view.ResetOriginToCursor();
                                view.Render(buffer, cursor);
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
                    { cursor = 0; view.Render(buffer, cursor); break; }
                    if (key.Key == ConsoleKey.E
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    { cursor = buffer.Length; view.Render(buffer, cursor); break; }
                    // Ctrl-U — kill to start of line (Emacs / readline standard).
                    if (key.Key == ConsoleKey.U
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        buffer.Remove(0, cursor);
                        cursor = 0;
                        view.Render(buffer, cursor);
                        break;
                    }
                    // Ctrl-K — kill to end of line.
                    if (key.Key == ConsoleKey.K
                        && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        buffer.Remove(cursor, buffer.Length - cursor);
                        view.Render(buffer, cursor);
                        break;
                    }
                    // Plain printable character (includes non-ASCII
                    // letters). Insert at cursor.
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(cursor, key.KeyChar);
                        cursor++;
                        view.Render(buffer, cursor);
                    }
                    break;
            }
        }
    }

    /// <summary>Repaints a single logical input line that may wrap onto
    /// several terminal rows, keeping the hardware cursor aligned with
    /// the logical edit position across rows. One instance per
    /// <see cref="ReadLine"/> call; it owns the origin row and the
    /// high-water painted length.</summary>
    private sealed class LineView
    {
        private readonly string _prompt;
        private int _originRow;   // console row where prompt char 0 sits
        private int _dirty;       // max cells ever painted — drives erase

        public LineView(string prompt, int originRow)
        {
            _prompt = prompt;
            _originRow = originRow;
        }

        /// <summary>Re-anchor the view at the current cursor row (col 0)
        /// after something else (e.g. a candidate listing) wrote below
        /// the line. The next <see cref="Render"/> repaints there.</summary>
        public void ResetOriginToCursor()
        {
            try { _originRow = Console.CursorTop; }
            catch { /* keep previous origin */ }
            _dirty = 0;
        }

        public void Render(StringBuilder buffer, int cursor)
        {
            int w = TerminalWidthOrDefault();
            int contentLen = _prompt.Length + buffer.Length;
            // Paint up to the high-water mark so any longer previous
            // render is fully blanked; bump the mark.
            int paintLen = Math.Max(contentLen, _dirty);
            _dirty = Math.Max(_dirty, contentLen);

            // Hide the cursor across the repaint. Without this the cursor
            // is visible jumping to column 0 (the origin) and back to the
            // edit point on every keystroke — a distracting flicker.
            bool hidden = TryHideCursor();
            try
            {
                if (!TrySetCursor(0, _originRow)) return;

                // Single write: prompt + buffer + trailing blanks. One call
                // minimizes flicker and lets the console do all wrapping.
                var sb = new StringBuilder(paintLen);
                sb.Append(_prompt).Append(buffer);
                if (paintLen > contentLen) sb.Append(' ', paintLen - contentLen);
                Console.Write(sb.ToString());

                AdjustOriginForScroll(paintLen, w);

                // Position the hardware cursor at the logical edit point.
                var (row, col) = CellRowCol(_prompt.Length + cursor, w);
                TrySetCursor(col, _originRow + row);
            }
            finally
            {
                if (hidden) TryShowCursor();
            }
        }

        /// <summary>Park the cursor just past the last painted cell (end
        /// of the wrapped line), used before emitting a newline.</summary>
        public void MoveToEnd(int bufferLength)
        {
            int w = TerminalWidthOrDefault();
            var (row, col) = CellRowCol(_prompt.Length + bufferLength, w);
            TrySetCursor(col, _originRow + row);
        }

        /// <summary>After painting <paramref name="paintLen"/> cells from
        /// the origin, the line ends on row <c>origin + paintLen/w</c>. If
        /// that exceeds the buffer's last row the terminal scrolled the
        /// region up; shift the origin by the overflow so later cursor
        /// math stays aligned. Computing the end row from the paint length
        /// (not the post-write <c>CursorTop</c>) sidesteps the deferred-wrap
        /// "phantom column" ambiguity at exact-width boundaries.</summary>
        private void AdjustOriginForScroll(int paintLen, int w)
        {
            int endRow = _originRow + paintLen / w;
            int bottom;
            try { bottom = Console.BufferHeight - 1; }
            catch { return; }
            if (endRow > bottom) _originRow -= (endRow - bottom);
        }
    }

    /// <summary>Pure helper: the (row, col) of the cell at linear
    /// <paramref name="linearIndex"/> in a region that starts at column 0
    /// and wraps every <paramref name="width"/> columns. Row is relative to
    /// the region's first row. Drives cursor placement and scroll
    /// detection; unit-tested in lieu of the interactive paint.</summary>
    public static (int Row, int Col) CellRowCol(int linearIndex, int width)
    {
        if (width < 1) width = 1;
        if (linearIndex < 0) linearIndex = 0;
        return (linearIndex / width, linearIndex % width);
    }

    private static bool TrySetCursor(int column, int row)
    {
        if (column < 0) column = 0;
        if (row < 0) row = 0;
        try { Console.SetCursorPosition(column, row); return true; }
        catch (System.IO.IOException) { return false; }      // not interactive
        catch (ArgumentOutOfRangeException) { return false; } // past the edge
    }

    /// <summary>Hide the cursor for the duration of a repaint so its
    /// transit to column 0 and back isn't visible. Returns whether the
    /// host honoured it (so we only re-show when we hid).</summary>
    private static bool TryHideCursor()
    {
        try { Console.CursorVisible = false; return true; }
        catch { return false; }
    }

    private static void TryShowCursor()
    {
        try { Console.CursorVisible = true; }
        catch { /* host doesn't support cursor visibility */ }
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

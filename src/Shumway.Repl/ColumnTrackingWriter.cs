using System.IO;
using System.Text;

namespace Shumway.Repl;

/// <summary>
/// Wraps the console's output writer and remembers whether the cursor is at the
/// start of a line — i.e. whether the last character written was a newline (or
/// nothing has been written yet). The top-level uses this to decide whether to
/// start an answer (<c>true</c> / <c>false</c> / bindings) on a fresh line when a
/// goal left the cursor mid-line, SWI-style.
///
/// <para>Column tracking is done on the bytes we write, not by reading
/// <c>Console.CursorLeft</c>, so it works identically whether output is a
/// terminal or is redirected / captured (a pipe has no queryable cursor).</para>
/// </summary>
internal sealed class ColumnTrackingWriter : TextWriter
{
    private readonly TextWriter _inner;

    /// <summary>True when the next character would be written at column 0 — the
    /// last character written was a line feed, or nothing has been written.</summary>
    public bool AtLineStart { get; private set; } = true;

    public ColumnTrackingWriter(TextWriter inner) => _inner = inner;

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        _inner.Write(value);
        AtLineStart = value == '\n';
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _inner.Write(value);
        AtLineStart = value[^1] == '\n';
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (count <= 0) return;
        _inner.Write(buffer, index, count);
        AtLineStart = buffer[index + count - 1] == '\n';
    }

    public override void Flush() => _inner.Flush();
}

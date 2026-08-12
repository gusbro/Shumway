namespace Shumway.Repl;

/// <summary>The REPL's shared input buffer: text the user typed that no query
/// has consumed yet. The top level takes SENTENCES from it (so
/// <c>write(a). nl.</c> on one line runs as two queries), and a goal reading
/// <c>user_input</c> (<c>read/1</c>, <c>get_char/1</c>) drains the SAME
/// buffer — <c>?- read(X). write(b).</c> binds <c>X = write(b)</c>, the
/// stream-fed top-level behaviour (SWI). When the buffer runs dry mid-goal,
/// more input is acquired from the console via the callback.</summary>
internal sealed class ReplPendingReader : System.IO.TextReader
{
    private readonly System.Text.StringBuilder _buf = new();
    private int _head;
    private readonly Func<string?> _acquire;

    public ReplPendingReader(Func<string?> acquire) => _acquire = acquire;

    /// <summary>Appends typed text to the buffer (include the newline).</summary>
    public void Push(string text) => _buf.Append(text);

    /// <summary>The buffered, not-yet-consumed text.</summary>
    public string Buffered { get { Compact(); return _buf.ToString(); } }

    /// <summary>Marks the first <paramref name="count"/> buffered chars consumed.</summary>
    public void Consume(int count) { _head += count; Compact(); }

    public void Clear() { _buf.Clear(); _head = 0; }

    private void Compact()
    {
        if (_head == 0) return;
        _buf.Remove(0, _head);
        _head = 0;
    }

    public override int Peek() => EnsureData() ? _buf[_head] : -1;

    public override int Read() => EnsureData() ? _buf[_head++] : -1;

    private bool EnsureData()
    {
        while (_head >= _buf.Length)
        {
            Compact();
            string? line = _acquire();
            if (line is null) return false;
            _buf.Append(line).Append('\n');
        }
        return true;
    }
}

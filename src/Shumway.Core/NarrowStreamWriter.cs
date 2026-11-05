using System.IO;
using System.Text;

namespace Shumway.Core;

/// <summary>
/// The writer of a text stream whose encoding holds only part of Unicode. A character
/// outside it raises <c>representation_error(character)</c> at the write that gives it,
/// not when the buffer is flushed. A <see cref="StreamWriter"/> alone would replace it
/// with <c>?</c>.
/// </summary>
public sealed class NarrowStreamWriter : StreamWriter
{
    private readonly char _last;

    /// <param name="stream">The file.</param>
    /// <param name="encoding">The encoding of the stream.</param>
    /// <param name="last">The last character the encoding holds.</param>
    public NarrowStreamWriter(Stream stream, Encoding encoding, char last)
        : base(stream, encoding) => _last = last;

    private void Check(char c)
    {
        if (c > _last) throw new PrologRuntimeException("representation_error", "character");
    }

    private void Check(string? text)
    {
        if (text is null) return;
        foreach (char c in text) Check(c);
    }

    private void Check(char[] buffer, int index, int count)
    {
        for (int i = index; i < index + count; i++) Check(buffer[i]);
    }

    public override void Write(char value)
    {
        Check(value);
        base.Write(value);
    }

    public override void Write(string? value)
    {
        Check(value);
        base.Write(value);
    }

    public override void Write(char[]? buffer)
    {
        if (buffer is not null) Check(buffer, 0, buffer.Length);
        base.Write(buffer);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        Check(buffer, index, count);
        base.Write(buffer, index, count);
    }

    public override void WriteLine(string? value)
    {
        Check(value);
        base.WriteLine(value);
    }

#if !NETFRAMEWORK
    public override void Write(System.ReadOnlySpan<char> buffer)
    {
        foreach (char c in buffer) Check(c);
        base.Write(buffer);
    }

    public override void WriteLine(System.ReadOnlySpan<char> buffer)
    {
        foreach (char c in buffer) Check(c);
        base.WriteLine(buffer);
    }
#endif
}

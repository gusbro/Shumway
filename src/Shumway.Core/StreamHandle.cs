using System.IO;

namespace Shumway.Core;

/// <summary>
/// A registered Prolog stream. Wraps either a <see cref="TextReader"/>
/// or a <see cref="TextWriter"/> (never both) along with the metadata
/// ISO §8.11 wants reflective access to: mode, alias, source filename
/// (when applicable), and a per-handle id used both for the
/// <c>Foreign</c> cell payload and for ordered enumeration via
/// <c>current_stream/3</c>.
///
/// <para>Stream handles live in <see cref="StreamRegistry"/> on the
/// hosting <see cref="Shumway.Embedding.PrologEngine"/>. The two
/// terminal-default handles — <c>user_input</c> and <c>user_output</c>
/// — are always present; <c>open/3</c> registers a new handle and
/// <c>close/1</c> deregisters it.</para>
/// </summary>
public sealed class StreamHandle
{
    public int Id { get; }

    /// <summary>The underlying reader, or null when this is a writer
    /// or binary handle.</summary>
    public TextReader? Reader { get; }

    /// <summary>The underlying writer, or null when this is a reader
    /// or binary handle.</summary>
    public TextWriter? Writer { get; }

    /// <summary>The underlying raw byte stream, or null when this
    /// is a text handle. Set for streams opened with the
    /// <c>type(binary)</c> option; ISO §8.13's byte I/O builtins
    /// read and write through this.</summary>
    public Stream? BinaryStream { get; }

    /// <summary>True for a stream opened with <c>type(binary)</c>.
    /// Byte builtins (<c>get_byte</c>, <c>put_byte</c>, …) require
    /// this; char builtins on a binary stream raise
    /// <c>permission_error(input, text_stream, _)</c>.</summary>
    public bool IsBinary => BinaryStream is not null;

    /// <summary>The mode this stream was opened in — <c>read</c>,
    /// <c>write</c>, or <c>append</c>.</summary>
    public string Mode { get; }

    /// <summary>The path passed to <c>open/3</c> if this is a file
    /// stream; null for the user-terminal defaults.</summary>
    public string? Filename { get; }

    /// <summary>The optional alias set via <c>open/4</c>'s
    /// <c>alias(Name)</c> option. A handle can be referred to by its
    /// alias atom anywhere a stream is required.</summary>
    public string? Alias { get; internal set; }

    /// <summary>True once <c>close/1</c> has run; the handle stays
    /// in the registry briefly so an inadvertent second-close can
    /// report <c>existence_error</c> rather than crashing.</summary>
    public bool Closed { get; internal set; }

    public bool IsReader => Reader is not null
        || (BinaryStream is not null && Mode == "read");
    public bool IsWriter => Writer is not null
        || (BinaryStream is not null && (Mode == "write" || Mode == "append"));

    public StreamHandle(int id, TextReader reader, string mode, string? filename = null, string? alias = null)
    {
        Id = id;
        // Every text read handle tracks its logical
        // character position (see PositionTrackingReader below).
        Reader = reader as PositionTrackingReader ?? new PositionTrackingReader(reader);
        Mode = mode;
        Filename = filename;
        Alias = alias;
    }

    public StreamHandle(int id, TextWriter writer, string mode, string? filename = null, string? alias = null)
    {
        Id = id;
        Writer = writer;
        Mode = mode;
        Filename = filename;
        Alias = alias;
    }

    /// <summary>Binary-stream constructor. <paramref name="binaryStream"/>
    /// is read from for <c>read</c> mode, written to for <c>write</c>
    /// / <c>append</c>. The text-side <see cref="Reader"/> /
    /// <see cref="Writer"/> stay null.</summary>
    public StreamHandle(int id, Stream binaryStream, string mode, string? filename = null, string? alias = null)
    {
        Id = id;
        BinaryStream = binaryStream;
        Mode = mode;
        Filename = filename;
        Alias = alias;
    }
}

/// <summary>A <see cref="TextReader"/> decorator that
/// counts the characters consumed, giving text read streams a logical
/// position the <c>StreamReader</c>'s internal read-ahead buffering cannot
/// spoil (a <c>Peek()</c> — e.g. for <c>stream_property/2</c>'s
/// <c>end_of_stream</c> — fills the buffer, which moves
/// <c>BaseStream.Position</c> to the end of the buffered block, so the raw
/// byte position over-reports by up to a buffer's worth).
/// <c>position(N)</c> reports <see cref="CharsConsumed"/>;
/// <c>set_stream_position/2</c> rewinds the base stream and re-consumes
/// <c>N</c> characters — O(N), but correct for any encoding.
/// Only <c>Peek</c>/<c>Read</c> are overridden: <c>TextReader</c>'s base
/// <c>ReadLine</c>/<c>ReadToEnd</c>/<c>ReadBlock</c> loop over them, so the
/// count stays exact for every read shape.</summary>
public sealed class PositionTrackingReader : TextReader
{
    /// <summary>The wrapped reader (typically a <c>StreamReader</c> for a
    /// file stream, or <c>Console.In</c> for <c>user_input</c>).</summary>
    public TextReader Inner { get; }

    /// <summary>Characters consumed so far — the stream's logical
    /// position.</summary>
    public long CharsConsumed { get; private set; }

    public PositionTrackingReader(TextReader inner) => Inner = inner;

    public override int Peek() => Inner.Peek();

    public override int Read()
    {
        int c = Inner.Read();
        if (c >= 0) CharsConsumed++;
        return c;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        int n = Inner.Read(buffer, index, count);
        if (n > 0) CharsConsumed += n;
        return n;
    }

    /// <summary>Resets the consumed-character count after the caller has
    /// rewound the underlying stream (see <c>set_stream_position/2</c>).</summary>
    public void ResetCount() => CharsConsumed = 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }
}

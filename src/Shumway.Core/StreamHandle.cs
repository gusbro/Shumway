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
    public PositionTrackingReader? Reader { get; }

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

    /// <summary>Engine encoding name of a TEXT stream (utf8, iso_latin_1,
    /// ascii, utf16le/be, utf32le/be) — what stream_property/2 reports.
    /// Null on binary streams.</summary>
    public string? EncodingName { get; set; }

    /// <summary>Whether the stream began with a byte order mark (read side:
    /// detected; write side: written). Null when unknown / not applicable
    /// (console streams, binary).</summary>
    public bool? HadBom { get; set; }

    /// <summary>The <c>eof_action</c> stream option: <c>eof_code</c>
    /// (default — reads at/past eof keep yielding <c>end_of_file</c>),
    /// <c>error</c> (a read PAST eof raises
    /// <c>permission_error(input, past_end_of_stream, S)</c>), or
    /// <c>reset</c>.</summary>
    public string EofAction { get; set; } = "eof_code";

    /// <summary>True once a read consumed the end of the stream — the
    /// position is past-end-of-stream (<c>end_of_stream(past)</c>).
    /// Peeks never set this.</summary>
    public bool PastEof { get; set; }

    /// <summary><c>reposition(false)</c> was passed to open/4: the stream
    /// refuses set_stream_position/2 even when the underlying stream could
    /// seek. Defaults to true — a seekable file is repositionable.</summary>
    public bool Repositionable { get; set; } = true;

    // ----- lazy text windows (ADR-047 phrase_from_stream) -----
    //
    // Reading is a side effect and backtracking cannot undo it, so a lazy
    // window has to be IDEMPOTENT: waking the same cell twice — which happens
    // whenever a grammar tries one clause, fails, and tries the next — must
    // hand back the same characters, not the ones after them. One window is
    // cached at a time, keyed by its character offset, which is all a re-run
    // ever asks for and keeps the memory bounded.
    public long LazyWindowOffset { get; set; } = -1;
    public string? LazyWindow { get; set; }
    /// <summary>Characters consumed from this stream by the lazy reader — the
    /// offset the next unread window starts at.</summary>
    public long LazyRead { get; set; }

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
    /// <summary>Sentinel for "nothing buffered" — distinct from -1, which is
    /// a real end-of-input answer.</summary>
    private const int Empty = int.MinValue;

    /// <summary>Characters produced ahead of their <see cref="Read"/>: the
    /// CR-translation path buffers one (see <see cref="BufferAcrossCr"/>) and
    /// <see cref="PeekCodePoint"/> pushes back a surrogate pair, so two slots.
    /// End-of-input is deliberately NOT buffered, because a reader like the
    /// REPL's may answer -1 now and yield more once the user types.</summary>
    private int _buffered = Empty;
    private int _buffered2 = Empty;

    /// <summary>The wrapped reader (typically a <c>StreamReader</c> for a
    /// file stream, or <c>Console.In</c> for <c>user_input</c>).</summary>
    public TextReader Inner { get; }

    /// <summary>Characters consumed so far — the stream's logical
    /// position.</summary>
    public long CharsConsumed { get; private set; }

    /// <summary>True when a CR-LF pair reads as the single character
    /// <c>\n</c> (ADR-045). Binary streams never reach this class, so the
    /// ISO rule "text converts, binary does not" is structural here.</summary>
    public bool TranslatesNewlines { get; }

    /// <summary>The platform default: on Windows a text stream's line
    /// terminator is CR-LF, and C stdio's text mode — hence GNU Prolog —
    /// presents it to the program as <c>\n</c>. Elsewhere the external form
    /// already IS <c>\n</c> and nothing is translated.</summary>
    public static bool TranslateNewlinesByDefault => OperatingSystem.IsWindows();

    public PositionTrackingReader(TextReader inner)
        : this(inner, TranslateNewlinesByDefault) { }

    public PositionTrackingReader(TextReader inner, bool translateNewlines)
    {
        Inner = inner;
        TranslatesNewlines = translateNewlines;
    }

    /// <summary>Consumes the CR the caller has already peeked and returns the
    /// character it stands for: <c>\n</c> when an LF follows, the CR itself
    /// otherwise. A LONE CR is data — only the pair is a line terminator,
    /// matching C stdio (a classic-Mac file therefore reads unchanged).</summary>
    private int BufferAcrossCr()
    {
        int c = Inner.Read();
        if (c < 0) return -1;
        if (c == '\r' && PeekAfterCr() == '\n') { Inner.Read(); c = '\n'; }
        return _buffered = c;
    }

    /// <summary>The LF look-ahead behind a CR. An ill-formed sequence there
    /// (strict UTF-8 reader) must not lose the CR already in hand: answer
    /// "not an LF" and let the error surface on the NEXT read, which is the
    /// one positioned at the offending bytes.</summary>
    private int PeekAfterCr()
    {
        try { return Inner.Peek(); }
        catch (PrologRuntimeException) { return -1; }
    }

    public override int Peek()
    {
        if (_buffered != Empty) return _buffered;
        if (!TranslatesNewlines) return Inner.Peek();
        int p = Inner.Peek();
        // Only a CR forces a consuming look-ahead; every other character is
        // answered without touching the stream, so a reader whose Peek is the
        // cheap "is input available" probe keeps that property.
        return p == '\r' ? BufferAcrossCr() : p;
    }

    public override int Read()
    {
        int c;
        if (_buffered != Empty)
        {
            c = _buffered;
            _buffered = _buffered2;
            _buffered2 = Empty;
        }
        else
        {
            c = Inner.Read();
            if (c == '\r' && TranslatesNewlines && PeekAfterCr() == '\n')
            {
                Inner.Read();
                c = '\n';
            }
        }
        if (c >= 0) CharsConsumed++;
        return c;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        if (!TranslatesNewlines && _buffered == Empty)
        {
            int n = Inner.Read(buffer, index, count);
            if (n > 0) CharsConsumed += n;
            return n;
        }
        int i = 0;
        while (i < count)
        {
            int c = Read();
            if (c < 0) break;
            buffer[index + i++] = (char)c;
        }
        return i;
    }

    /// <summary>Buffer-aware peek that swallows a strict-decode error: the
    /// pushed-back half must not be lost, and the error re-surfaces on the
    /// NEXT read, which is the one positioned at the offending bytes.</summary>
    private int PeekChecked()
    {
        try { return Peek(); }
        catch (PrologRuntimeException) { return -1; }
    }

    /// <summary>Reads one CODE POINT: a surrogate pair is joined into its
    /// astral value (the strict UTF-8 reader decodes full code points and
    /// presents them as pairs — this is where the char layer re-joins them).
    /// A lone surrogate reads unit-wise rather than throwing, matching
    /// malformed-atom policy everywhere else.</summary>
    public int ReadCodePoint()
    {
        int c = Read();
        if (c < 0 || !char.IsHighSurrogate((char)c)) return c;
        int l = PeekChecked();
        if (l >= 0 && char.IsLowSurrogate((char)l))
        {
            Read();
            return char.ConvertToUtf32((char)c, (char)l);
        }
        return c;
    }

    /// <summary>Peeks one CODE POINT without consuming it. Seeing an astral
    /// character's low half forces consuming the high half; both units are
    /// pushed back and the consumed count restored, so the operation is a
    /// true peek.</summary>
    public int PeekCodePoint()
    {
        int c = Peek();
        if (c < 0 || !char.IsHighSurrogate((char)c)) return c;
        long before = CharsConsumed;
        int h = Read();
        int l = PeekChecked();
        if (l >= 0 && char.IsLowSurrogate((char)l))
        {
            Read();
            _buffered = h;
            _buffered2 = l;
            CharsConsumed = before;
            return char.ConvertToUtf32((char)h, (char)l);
        }
        _buffered = h;
        CharsConsumed = before;
        return h;
    }

    /// <summary>Resets the consumed-character count after the caller has
    /// rewound the underlying stream (see <c>set_stream_position/2</c>).</summary>
    public void ResetCount()
    {
        CharsConsumed = 0;
        _buffered = Empty;
        _buffered2 = Empty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }
}

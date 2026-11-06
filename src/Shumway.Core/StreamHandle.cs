using System.IO;

namespace Shumway.Core;

/// <summary>
/// A registered Prolog stream. Wraps a <see cref="TextReader"/>, a
/// <see cref="TextWriter"/>, both for a stream of two directions, or a
/// byte stream, along with the metadata
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
    /// <c>write</c>, <c>append</c>, or <c>readwrite</c> for one that goes both
    /// ways, such as a socket.</summary>
    public string Mode { get; }

    /// <summary>The path passed to <c>open/3</c> if this is a file
    /// stream; null for the user-terminal defaults.</summary>
    public string? Filename { get; }

    private readonly List<string> _aliases = new(1);

    /// <summary>The aliases that name this stream: the one <c>open/4</c>'s
    /// <c>alias(Name)</c> option gave it and those <c>set_stream/2</c> added.
    /// A handle can be referred to by any of them anywhere a stream is
    /// required. An alias names one stream at a time.</summary>
    public IReadOnlyList<string> Aliases => _aliases;

    /// <summary>The first of <see cref="Aliases"/>, or null.</summary>
    public string? Alias => _aliases.Count > 0 ? _aliases[0] : null;

    internal void AddAlias(string alias)
    {
        if (!_aliases.Contains(alias)) _aliases.Add(alias);
    }

    internal void RemoveAlias(string alias) => _aliases.Remove(alias);

    /// <summary>Engine encoding name of a text stream (utf8, iso_latin_1,
    /// ascii, utf16, utf16le/be, utf32, utf32le/be), from which stream_property/2
    /// reports the official name.
    /// Null on binary streams.</summary>
    public string? EncodingName { get; set; }

    /// <summary>Whether the stream began with a byte order mark (read side:
    /// detected; write side: written). Null when unknown / not applicable
    /// (console streams, binary).</summary>
    public bool? HadBom { get; set; }

    /// <summary>True when a read can wait on the other end of a pipe or a socket, so
    /// that a question about the stream's state must not read to answer it.</summary>
    public bool BlocksOnRead { get; init; }

    /// <summary>The pipe or socket behind the stream, if that is what it is.</summary>
    public LiveStream? LiveConnection { get; set; }

    /// <summary>The socket behind the stream, if that is what it is.</summary>
    public System.Net.Sockets.Socket? Socket { get; set; }

    private readonly object _copyGate = new();
    private int _copies;
    private Action? _closeAfterCopies;

    /// <summary>A copy to or from a child process starts using the stream. The program can
    /// close the stream meanwhile, as it can a descriptor the child also holds: the real
    /// close waits for the last copy.</summary>
    public void CopyStarted()
    {
        lock (_copyGate) _copies++;
    }

    /// <summary>A copy is over; if the program closed the stream, this closes it.</summary>
    public void CopyEnded()
    {
        Action? close = null;
        lock (_copyGate)
        {
            if (--_copies == 0)
            {
                close = _closeAfterCopies;
                _closeAfterCopies = null;
            }
        }
        close?.Invoke();
    }

    /// <summary>Leaves the real close for the last copy, if one is still running.</summary>
    public bool TryCloseAfterCopies(Action close)
    {
        lock (_copyGate)
        {
            if (_copies == 0) return false;
            _closeAfterCopies = close;
            return true;
        }
    }

    /// <summary>Takes the bytes read ahead of the program's reads, for a copy to send first.</summary>
    public byte[] TakeInputBuffered()
    {
        if (_peekedByte != NoPeekedByte)
        {
            int peeked = _peekedByte;
            _peekedByte = NoPeekedByte;
            return peeked < 0 ? Array.Empty<byte>() : new[] { (byte)peeked };
        }
        if (Reader is null) return Array.Empty<byte>();
        string decoded = Reader.TakeDecodedAhead();
        byte[] first = decoded.Length == 0 ? Array.Empty<byte>()
            : (TextEncodings.ByName(EncodingName ?? "utf8") ?? System.Text.Encoding.UTF8).GetBytes(decoded);
        byte[] rest = Reader.Inner is StrictTextReader strict ? strict.TakeBuffered() : Array.Empty<byte>();
        byte[] all = new byte[first.Length + rest.Length];
        Buffer.BlockCopy(first, 0, all, 0, first.Length);
        Buffer.BlockCopy(rest, 0, all, first.Length, rest.Length);
        return all;
    }

    /// <summary>The host name the stream connected to, if it did.</summary>
    public string? PeerHost { get; set; }

    /// <summary>The encoding the program named for the stream, if it did.</summary>
    public string? NamedEncoding { get; set; }

    /// <summary>Whether input has already reached the stream's own buffers, so that a
    /// read will not wait on the connection.</summary>
    public bool HasBufferedInput => _peekedByte != NoPeekedByte || (Reader?.HasBuffered ?? false);

    /// <summary>The <c>eof_action</c> stream option: <c>eof_code</c>
    /// (a handle's default; open/3,4 set <c>StreamRegistry.DefaultEofAction</c>.
    /// Reads at/past eof keep yielding <c>end_of_file</c>),
    /// <c>error</c> (a read past eof raises
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
    // window has to be idempotent: waking the same cell twice — which happens
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
        || (BinaryStream is not null && Mode is "read" or "readwrite");
    public bool IsWriter => Writer is not null
        || (BinaryStream is not null && Mode is "write" or "append" or "readwrite");

    public StreamHandle(int id, TextReader reader, string mode, string? filename = null, string? alias = null)
    {
        Id = id;
        // Every text read handle tracks its logical
        // character position (see PositionTrackingReader below).
        Reader = reader as PositionTrackingReader ?? new PositionTrackingReader(reader);
        Mode = mode;
        Filename = filename;
        if (alias is not null) _aliases.Add(alias);
    }

    public StreamHandle(int id, TextWriter writer, string mode, string? filename = null, string? alias = null)
    {
        Id = id;
        Writer = writer;
        Mode = mode;
        Filename = filename;
        if (alias is not null) _aliases.Add(alias);
    }

    /// <summary>A text stream of two directions over one connection.</summary>
    public StreamHandle(int id, TextReader reader, TextWriter writer, string mode,
        string? filename = null, string? alias = null)
        : this(id, reader, mode, filename, alias)
    {
        Writer = writer;
    }

    private const int NoPeekedByte = int.MinValue;
    private int _peekedByte = NoPeekedByte;

    /// <summary>The next byte of a binary stream, or -1 at its end.</summary>
    public int ReadByte()
    {
        if (_peekedByte == NoPeekedByte) return BinaryStream!.ReadByte();
        int b = _peekedByte;
        _peekedByte = NoPeekedByte;
        return b;
    }

    /// <summary>The next byte of a binary stream without consuming it, or -1 at its
    /// end. A stream that cannot seek keeps it for the next read.</summary>
    public int PeekByte()
    {
        if (_peekedByte != NoPeekedByte) return _peekedByte;
        Stream bs = BinaryStream!;
        if (!bs.CanSeek) return _peekedByte = bs.ReadByte();
        long pos = bs.Position;
        int b = bs.ReadByte();
        bs.Position = pos;
        return b;
    }

    /// <summary>Binary-stream constructor. <paramref name="binaryStream"/>
    /// is read from for <c>read</c> mode, written to for <c>write</c>
    /// / <c>append</c>, and both for <c>readwrite</c>. The text-side <see cref="Reader"/> /
    /// <see cref="Writer"/> stay null.</summary>
    public StreamHandle(int id, Stream binaryStream, string mode, string? filename = null, string? alias = null)
    {
        Id = id;
        BinaryStream = binaryStream;
        Mode = mode;
        Filename = filename;
        if (alias is not null) _aliases.Add(alias);
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
    /// End-of-input is deliberately not buffered, because a reader like the
    /// REPL's may answer -1 now and yield more once the user types.</summary>
    private int _buffered = Empty;
    private int _buffered2 = Empty;

    /// <summary>Whether characters or bytes are held here or in the reader below, ahead
    /// of their read.</summary>
    internal bool HasBuffered => _buffered != Empty || (Inner is StrictTextReader { HasBuffered: true });

    /// <summary>Takes the characters a peek decoded ahead of their read: one past the BMP,
    /// or a line end already translated.</summary>
    internal string TakeDecodedAhead()
    {
        string ahead = (_buffered != Empty ? ((char)_buffered).ToString() : "")
            + (_buffered2 != Empty ? ((char)_buffered2).ToString() : "");
        _buffered = Empty;
        _buffered2 = Empty;
        return ahead;
    }

    /// <summary>The wrapped reader (typically a <c>StreamReader</c> for a
    /// file stream, or <c>Console.In</c> for <c>user_input</c>).</summary>
    public TextReader Inner { get; }

    /// <summary>Characters consumed so far — the stream's logical
    /// position.</summary>
    public long CharsConsumed { get; private set; }

    /// <summary>The line the next character is on, counting from 1: one more
    /// than the line terminators consumed so far. A repositioning re-reads up
    /// to its target, so this stays exact without being stored in a
    /// position.</summary>
    public long Line { get; private set; } = 1;

    /// <summary>True when a CR-LF pair reads as the single character
    /// <c>\n</c> (ADR-045). Binary streams never reach this class, so the
    /// ISO rule "text converts, binary does not" is structural here.</summary>
    public bool TranslatesNewlines { get; }

    /// <summary>The platform default: on Windows a text stream's line
    /// terminator is CR-LF, and C stdio's text mode — hence GNU Prolog —
    /// presents it to the program as <c>\n</c>. Elsewhere the external form
    /// already is <c>\n</c> and nothing is translated.</summary>
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
    /// otherwise. A lone CR is data — only the pair is a line terminator,
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
    /// "not an LF" and let the error surface on the next read, which is the
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
        if (c >= 0)
        {
            CharsConsumed++;
            if (c == '\n') Line++;
        }
        return c;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        if (!TranslatesNewlines && _buffered == Empty)
        {
            int n = Inner.Read(buffer, index, count);
            if (n > 0)
            {
                CharsConsumed += n;
                for (int k = index; k < index + n; k++)
                    if (buffer[k] == '\n') Line++;
            }
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
    /// next read, which is the one positioned at the offending bytes.</summary>
    private int PeekChecked()
    {
        try { return Peek(); }
        catch (PrologRuntimeException) { return -1; }
    }

    /// <summary>Reads one code point: a surrogate pair is joined into its
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

    /// <summary>True when the wrapped reader is the host's standard input,
    /// the case where a key can be read from its console.</summary>
    internal bool ReadsHostInput { get; set; }

    /// <summary>Reads one code point for <c>get_unbuffered_char/1</c> and
    /// <c>get_unbuffered_code/1</c>: a key when this reader is a terminal,
    /// otherwise as <see cref="ReadCodePoint"/>. What an earlier peek pushed
    /// back was already consumed from the source, so it comes first.</summary>
    public int ReadKeyOrCodePoint()
    {
        if (_buffered != Empty || !TryReadKey(out int key)) return ReadCodePoint();
        if (key >= 0)
        {
            CharsConsumed += key > 0xFFFF ? 2 : 1;
            if (key == '\n') Line++;
        }
        return key;
    }

    private bool TryReadKey(out int key)
    {
        if (Inner is IKeyReader keys) return keys.TryReadKey(out key);
        if (ReadsHostInput) return TerminalKeys.TryRead(out key);
        key = -1;
        return false;
    }

    /// <summary>Peeks one code point without consuming it. Seeing an astral
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

    /// <summary>Resets the consumed-character and line counts after the
    /// caller has rewound the underlying stream (see
    /// <c>set_stream_position/2</c>).</summary>
    public void ResetCount()
    {
        CharsConsumed = 0;
        Line = 1;
        _buffered = Empty;
        _buffered2 = Empty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }
}

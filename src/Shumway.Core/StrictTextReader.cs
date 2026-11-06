using System;
using System.IO;

namespace Shumway.Core;

/// <summary>
/// Strict text reader for Prolog text streams, for every encoding the engine
/// names. Unlike <see cref="StreamReader"/> — whose decoder silently replaces
/// an ill-formed byte sequence with U+FFFD, indistinguishable from a genuine
/// U+FFFD in the input — this reader raises
/// <c>representation_error(character)</c> (WG17; Trealla/Scryer agree), with
/// the peek/read asymmetry the ISO stream model needs:
///
/// <list type="bullet">
/// <item><see cref="Peek"/> on an ill-formed sequence throws without
///   consuming — the peek stays repeatable and a later read sees the same
///   bytes.</item>
/// <item><see cref="Read"/> on an ill-formed sequence consumes one code unit of
///   the encoding (the offending lead byte in UTF-8) before throwing, so a
///   reader that catches the error can resynchronise.</item>
/// </list>
///
/// <para>A tolerant reader gives U+FFFD for that code unit instead of raising, for text
/// from a program or a peer whose encoding nobody can know.</para>
///
/// <para>An astral code point decodes to its surrogate pair, delivered as
/// two consecutive UTF-16 units — the <see cref="StreamReader"/> behaviour
/// the rest of the engine already expects. The text starts where the stream
/// stands when the reader is made: the open path has skipped a byte order mark
/// it detected, and one it did not look for reads as the character U+FEFF.</para>
/// </summary>
public sealed class StrictTextReader : TextReader
{
    private enum Form { Utf8, Latin1, Ascii, Utf16Le, Utf16Be, Utf32Le, Utf32Be }

    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[4096];
    private int _bufPos;
    private int _bufLen;
    private int _pendingLow = -1;
    private readonly long _start;
    private readonly Form _form;
    private readonly int _unit;
    private readonly bool _tolerant;

    /// <param name="stream">The file, positioned where the text starts.</param>
    /// <param name="encodingName">The engine name of the encoding.</param>
    /// <param name="tolerant">Whether an ill-formed sequence reads as U+FFFD.</param>
    public StrictTextReader(Stream stream, string encodingName, bool tolerant = false)
    {
        _stream = stream;
        _tolerant = tolerant;
        _start = stream.CanSeek ? stream.Position : 0;
        _form = encodingName switch
        {
            "utf8" => Form.Utf8,
            "iso_latin_1" => Form.Latin1,
            "ascii" => Form.Ascii,
            "utf16le" => Form.Utf16Le,
            "utf16" or "utf16be" => Form.Utf16Be,
            "utf32le" => Form.Utf32Le,
            "utf32" or "utf32be" => Form.Utf32Be,
            _ => throw new ArgumentException(encodingName, nameof(encodingName)),
        };
        _unit = _form switch
        {
            Form.Utf16Le or Form.Utf16Be => 2,
            Form.Utf32Le or Form.Utf32Be => 4,
            _ => 1,
        };
    }

    /// <summary>Gives up the bytes it read ahead, for whoever reads the stream next.</summary>
    internal byte[] TakeBuffered()
    {
        var held = new byte[_bufLen - _bufPos];
        Buffer.BlockCopy(_buf, _bufPos, held, 0, held.Length);
        _bufPos = _bufLen;
        return held;
    }

    /// <summary>Whether it holds input ahead of its reads.</summary>
    internal bool HasBuffered => _pendingLow >= 0 || _bufPos < _bufLen;

    /// <summary>The underlying stream — the reposition machinery seeks it
    /// (see <c>set_stream_position/2</c>).</summary>
    public Stream BaseStream => _stream;

    /// <summary>Rewinds to the start of the text, past a byte order mark that was
    /// detected, discarding every buffered byte and any pending surrogate half.</summary>
    public void Rewind()
    {
        _stream.Position = _start;
        _bufPos = 0;
        _bufLen = 0;
        _pendingLow = -1;
    }

    // Ensures at least `n` unread bytes are buffered (best effort — fewer
    // means end of input). Compacts so a sequence never straddles the end.
    private int Available(int n)
    {
        if (_bufLen - _bufPos >= n) return n;
        if (_bufPos > 0)
        {
            Buffer.BlockCopy(_buf, _bufPos, _buf, 0, _bufLen - _bufPos);
            _bufLen -= _bufPos;
            _bufPos = 0;
        }
        while (_bufLen - _bufPos < n)
        {
            int got = _stream.Read(_buf, _bufLen, _buf.Length - _bufLen);
            if (got <= 0) break;
            _bufLen += got;
        }
        return _bufLen - _bufPos;
    }

    private static PrologRuntimeException IllFormed()
        => new("representation_error", "character");

    /// <summary>Decodes the next code point from the buffer without
    /// consuming. Returns -1 at end of input; on success <paramref
    /// name="length"/> is the sequence's byte count. Throws (consuming
    /// nothing) on an ill-formed sequence.</summary>
    private int DecodeNext(out int length)
    {
        if (!_tolerant) return Decode(out length);
        try { return Decode(out length); }
        catch (PrologRuntimeException ex) when (ex.Kind == "representation_error")
        {
            length = Math.Min(_unit, _bufLen - _bufPos);
            return 0xFFFD;
        }
    }

    private int Decode(out int length)
    {
        if (Available(1) < 1) { length = 0; return -1; }
        if (_form != Form.Utf8) return DecodeOther(out length);
        int b0 = _buf[_bufPos];
        if (b0 < 0x80) { length = 1; return b0; }

        // Sequence length + constrained range of the first continuation
        // byte (RFC 3629 table: rejects overlongs, surrogates, > U+10FFFF).
        int need; int lo = 0x80; int hi = 0xBF;
        switch (b0)
        {
            case >= 0xC2 and <= 0xDF: need = 2; break;
            case 0xE0: need = 3; lo = 0xA0; break;
            case >= 0xE1 and <= 0xEC: need = 3; break;
            case 0xED: need = 3; hi = 0x9F; break;
            case 0xEE or 0xEF: need = 3; break;
            case 0xF0: need = 4; lo = 0x90; break;
            case >= 0xF1 and <= 0xF3: need = 4; break;
            case 0xF4: need = 4; hi = 0x8F; break;
            default: throw IllFormed();   // 0x80–0xC1, 0xF5–0xFF
        }
        if (Available(need) < need) throw IllFormed();   // truncated (EOF)
        int cp = b0 & (0xFF >> (need + 1));
        for (int i = 1; i < need; i++)
        {
            int bi = _buf[_bufPos + i];
            int l = i == 1 ? lo : 0x80;
            int h = i == 1 ? hi : 0xBF;
            if (bi < l || bi > h) throw IllFormed();
            cp = (cp << 6) | (bi & 0x3F);
        }
        length = need;
        return cp;
    }

    private int DecodeOther(out int length)
    {
        int b0 = _buf[_bufPos];
        switch (_form)
        {
            case Form.Latin1:
                length = 1;
                return b0;
            case Form.Ascii:
                if (b0 >= 0x80) throw IllFormed();
                length = 1;
                return b0;
            case Form.Utf16Le:
            case Form.Utf16Be:
                if (Available(2) < 2) throw IllFormed();
                int unit = Unit16(0);
                length = 2;
                if (unit is < 0xD800 or > 0xDFFF) return unit;
                if (unit >= 0xDC00 || Available(4) < 4) throw IllFormed();
                int low = Unit16(2);
                if (low is < 0xDC00 or > 0xDFFF) throw IllFormed();
                length = 4;
                return 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
            default:
                if (Available(4) < 4) throw IllFormed();
                int p = _bufPos;
                uint cp = _form == Form.Utf32Le
                    ? (uint)_buf[p] | ((uint)_buf[p + 1] << 8) | ((uint)_buf[p + 2] << 16) | ((uint)_buf[p + 3] << 24)
                    : ((uint)_buf[p] << 24) | ((uint)_buf[p + 1] << 16) | ((uint)_buf[p + 2] << 8) | _buf[p + 3];
                if (cp > 0x10FFFF || cp is >= 0xD800 and <= 0xDFFF) throw IllFormed();
                length = 4;
                return (int)cp;
        }
    }

    private int Unit16(int offset) => _form == Form.Utf16Le
        ? _buf[_bufPos + offset] | (_buf[_bufPos + offset + 1] << 8)
        : (_buf[_bufPos + offset] << 8) | _buf[_bufPos + offset + 1];

    public override int Peek()
    {
        if (_pendingLow >= 0) return _pendingLow;
        int cp = DecodeNext(out _);
        if (cp < 0) return -1;
        return cp >= 0x10000 ? 0xD800 + ((cp - 0x10000) >> 10) : cp;
    }

    public override int Read()
    {
        if (_pendingLow >= 0)
        {
            int low = _pendingLow;
            _pendingLow = -1;
            return low;
        }
        int cp;
        int length;
        try { cp = DecodeNext(out length); }
        // Not a failure of the file under it (system_error): no byte was read.
        catch (PrologRuntimeException ex) when (ex.Kind == "representation_error")
        {
            // A read consumes the offending code unit, where a peek left it in place.
            _bufPos += Math.Min(_unit, _bufLen - _bufPos);
            throw;
        }
        if (cp < 0) return -1;
        _bufPos += length;
        if (cp < 0x10000) return cp;
        _pendingLow = 0xDC00 + ((cp - 0x10000) & 0x3FF);
        return 0xD800 + ((cp - 0x10000) >> 10);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stream.Dispose();
        base.Dispose(disposing);
    }
}

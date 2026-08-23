using System;
using System.IO;

namespace Shumway.Core;

/// <summary>
/// Strict UTF-8 text reader for Prolog text streams. Unlike
/// <see cref="StreamReader"/> — whose decoder silently replaces an
/// ill-formed byte sequence with U+FFFD, indistinguishable from a genuine
/// U+FFFD in the input — this reader raises
/// <c>representation_error(character)</c> (WG17; Trealla/Scryer agree), with
/// the peek/read asymmetry the ISO stream model needs:
///
/// <list type="bullet">
/// <item><see cref="Peek"/> on an ill-formed sequence throws WITHOUT
///   consuming — the peek stays repeatable and a later read sees the same
///   bytes.</item>
/// <item><see cref="Read"/> on an ill-formed sequence consumes exactly ONE
///   byte (the offending lead) before throwing, so a reader that catches the
///   error can resynchronise byte-by-byte.</item>
/// </list>
///
/// <para>An astral code point decodes to its surrogate pair, delivered as
/// two consecutive UTF-16 units — the <see cref="StreamReader"/> behaviour
/// the rest of the engine already expects. A leading UTF-8 BOM is skipped;
/// UTF-16 inputs never reach this class (the open path sniffs their BOM and
/// keeps the auto-detecting <see cref="StreamReader"/> for them).</para>
/// </summary>
public sealed class Utf8TextReader : TextReader
{
    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[4096];
    private int _bufPos;
    private int _bufLen;
    private int _pendingLow = -1;
    private bool _bomChecked;

    public Utf8TextReader(Stream stream) => _stream = stream;

    /// <summary>The underlying stream — the reposition machinery seeks it
    /// (see <c>set_stream_position/2</c>).</summary>
    public Stream BaseStream => _stream;

    /// <summary>Rewinds to the start of the stream, discarding every
    /// buffered byte and any pending surrogate half.</summary>
    public void Rewind()
    {
        _stream.Position = 0;
        _bufPos = 0;
        _bufLen = 0;
        _pendingLow = -1;
        _bomChecked = false;
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

    private void SkipBomOnce()
    {
        if (_bomChecked) return;
        _bomChecked = true;
        if (Available(3) >= 3
            && _buf[_bufPos] == 0xEF && _buf[_bufPos + 1] == 0xBB
            && _buf[_bufPos + 2] == 0xBF)
            _bufPos += 3;
    }

    private static PrologRuntimeException IllFormed()
        => new("representation_error", "character");

    /// <summary>Decodes the next code point from the buffer without
    /// consuming. Returns -1 at end of input; on success <paramref
    /// name="length"/> is the sequence's byte count. Throws (consuming
    /// nothing) on an ill-formed sequence.</summary>
    private int DecodeNext(out int length)
    {
        SkipBomOnce();
        if (Available(1) < 1) { length = 0; return -1; }
        int b0 = _buf[_bufPos];
        if (b0 < 0x80) { length = 1; return b0; }

        // Sequence length + constrained range of the FIRST continuation
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
        catch (PrologRuntimeException)
        {
            // A READ consumes the offending lead byte — byte-wise resync —
            // where a PEEK left it in place.
            _bufPos++;
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

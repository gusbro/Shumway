using System.Text;

namespace Shumway.Core;

/// <summary>The text encodings the engine names in Prolog — <c>open/4</c>'s
/// <c>encoding(...)</c> option, <c>stream_property/2</c>'s report, and the
/// <c>:- encoding/1</c> source directive. One table, so every door agrees on
/// the spelling.</summary>
public static class TextEncodings
{
    /// <summary>Resolves an engine encoding name (<c>utf8</c>,
    /// <c>iso_latin_1</c>, <c>ascii</c>, <c>utf16le</c>, <c>utf16be</c>,
    /// <c>utf32le</c>, <c>utf32be</c>). The instances never emit a BOM —
    /// BOM writing is the caller's explicit decision.</summary>
    public static Encoding? ByName(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false),
        "iso_latin_1" => Encoding.Latin1,
        "ascii" => Encoding.ASCII,
        "utf16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        "utf16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        "utf32le" => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        "utf32be" => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        _ => null,
    };

    /// <summary>Maps a <c>:- encoding/1</c> directive argument to the engine
    /// name: engine names pass through; the quoted charset spellings other
    /// systems write in sources (Logtalk's <c>'UTF-8'</c>, SWI's legacy
    /// <c>unicode_le</c>) are accepted as aliases.</summary>
    public static string? DirectiveNameToEngineName(string name) => name switch
    {
        "utf8" or "iso_latin_1" or "ascii" or "utf16le" or "utf16be"
            or "utf32le" or "utf32be" => name,
        "UTF-8" => "utf8",
        "ISO-8859-1" => "iso_latin_1",
        "US-ASCII" => "ascii",
        "UTF-16LE" or "UCS-2LE" or "unicode_le" => "utf16le",
        "UTF-16BE" or "UCS-2BE" or "unicode_be" => "utf16be",
        "UTF-32LE" => "utf32le",
        "UTF-32BE" => "utf32be",
        _ => null,
    };

    /// <summary>Byte-order-mark sniff. Returns the engine encoding name and
    /// the BOM's byte length, or (null, 0). UTF-32LE is checked before
    /// UTF-16LE — its BOM starts with the same two bytes.</summary>
    public static (string? Name, int BomLength) SniffBom(byte[] bytes, int count)
    {
        if (count >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE
            && bytes[2] == 0x00 && bytes[3] == 0x00)
            return ("utf32le", 4);
        if (count >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00
            && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return ("utf32be", 4);
        if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return ("utf8", 3);
        if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return ("utf16le", 2);
        if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return ("utf16be", 2);
        return (null, 0);
    }

    /// <summary>Decodes without ever throwing: ill-formed input becomes
    /// U+FFFD. The tolerant mode source sniffing depends on — a file whose
    /// real encoding differs from the attempted one must still yield a
    /// string to look for the <c>:- encoding/1</c> directive in.</summary>
    public static string DecodeLenient(string name, byte[] bytes, int offset, int count)
    {
        Encoding enc = (ByName(name) ?? new UTF8Encoding(false));
        enc = (Encoding)enc.Clone();
        enc.DecoderFallback = DecoderFallback.ReplacementFallback;
        return enc.GetString(bytes, offset, count);
    }
}

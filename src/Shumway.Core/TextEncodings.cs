using System.Text;

namespace Shumway.Core;

/// <summary>The text encodings the engine names in Prolog — <c>open/4</c>'s
/// <c>encoding(...)</c> option, <c>stream_property/2</c>'s report, and the
/// <c>:- encoding/1</c> source directive. One table, so every door agrees on
/// the spelling.</summary>
public static class TextEncodings
{
    /// <summary>What <see cref="DecodeLenient"/> leaves where bytes are not valid in
    /// the encoding. A Unicode noncharacter, reserved for a program's internal use, so
    /// it does not stand for text a source file means: U+FFFD can be written on purpose.
    /// The lexer rejects it anywhere but in a comment.</summary>
    public const char IllFormedMark = '\uFDD0';

    /// <summary>Resolves an engine encoding name (<c>utf8</c>,
    /// <c>iso_latin_1</c>, <c>ascii</c>, <c>utf16</c>, <c>utf16le</c>, <c>utf16be</c>,
    /// <c>utf32</c>, <c>utf32le</c>, <c>utf32be</c>). The instances never emit a BOM —
    /// BOM writing is the caller's explicit decision.</summary>
    public static Encoding? ByName(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false),
        "iso_latin_1" => Encoding.Latin1,
        "ascii" => Encoding.ASCII,
        "utf16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        "utf16" or "utf16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        "utf32le" => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        "utf32" or "utf32be" => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        _ => null,
    };

    /// <summary>The name of a .NET encoding: its engine name when the engine has one,
    /// otherwise the one .NET knows it by, such as <c>windows-1252</c>.</summary>
    public static string NameOf(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "utf8",
        28591 => "iso_latin_1",
        20127 => "ascii",
        1200 => "utf16le",
        1201 => "utf16be",
        12000 => "utf32le",
        12001 => "utf32be",
        _ => encoding.WebName,
    };

    /// <summary>The official name of an encoding, the one IANA registers: what
    /// <c>stream_property/2</c> and the <c>encoding</c> flag report.</summary>
    public static string OfficialName(string engineName) => engineName switch
    {
        "utf8" => "UTF-8",
        "iso_latin_1" => "ISO-8859-1",
        "ascii" => "US-ASCII",
        "utf16" => "UTF-16",
        "utf16le" => "UTF-16LE",
        "utf16be" => "UTF-16BE",
        "utf32" => "UTF-32",
        "utf32le" => "UTF-32LE",
        "utf32be" => "UTF-32BE",
        _ => engineName,
    };

    /// <summary>The engine name of an encoding, from any of its names: the official
    /// one, an alias or the engine name itself. Null for a name of no encoding.</summary>
    public static string? NameToEngineName(string name) => name switch
    {
        "utf8" or "iso_latin_1" or "ascii" or "utf16" or "utf16le" or "utf16be"
            or "utf32" or "utf32le" or "utf32be" => name,
        "UTF-8" => "utf8",
        "ISO-8859-1" => "iso_latin_1",
        "US-ASCII" => "ascii",
        "UTF-16LE" or "UCS-2LE" or "unicode_le" => "utf16le",
        "UTF-16BE" or "UCS-2BE" or "unicode_be" => "utf16be",
        "UTF-16" or "UCS-2" => "utf16",
        "UTF-32LE" => "utf32le",
        "UTF-32BE" => "utf32be",
        "UTF-32" => "utf32",
        _ => null,
    };

    /// <summary>Whether the encoding leaves the byte order to a byte order mark: a file
    /// with none is big-endian, and one written from the start gets one.</summary>
    public static bool LeavesByteOrderToMark(string engineName) => engineName is "utf16" or "utf32";

    /// <summary>Whether the encoding can write a byte order mark at all.</summary>
    public static bool IsUnicode(string engineName) => engineName is not ("iso_latin_1" or "ascii");

    /// <summary>Whether two engine names give the same bytes: <c>utf16</c> reads and
    /// writes as <c>utf16be</c> does, and <c>utf32</c> as <c>utf32be</c>.</summary>
    public static bool SameBytes(string a, string b) => a == b || BigEndianForm(a) == BigEndianForm(b);

    private static string BigEndianForm(string engineName) => engineName switch
    {
        "utf16" => "utf16be",
        "utf32" => "utf32be",
        _ => engineName,
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
    /// <see cref="IllFormedMark"/>. The tolerant mode source sniffing depends on — a file whose
    /// real encoding differs from the attempted one must still yield a
    /// string to look for the <c>:- encoding/1</c> directive in.</summary>
    public static string DecodeLenient(string name, byte[] bytes, int offset, int count)
    {
        Encoding enc = (ByName(name) ?? new UTF8Encoding(false));
        enc = (Encoding)enc.Clone();
        enc.DecoderFallback = new DecoderReplacementFallback(IllFormedMark.ToString());
        return enc.GetString(bytes, offset, count);
    }
}

namespace Shumway.Core;

/// <summary>Reading a whole file the way a Prolog TEXT stream reads it
/// (ADR-045). <c>consult/1</c>, <c>:- include/1</c> and the offline
/// compilers slurp a source file with <c>File.ReadAllText</c> rather than
/// through a stream handle; without this they would see the CR that
/// <c>open/3</c> + <c>read/2</c> hides, so the same file could yield two
/// different quoted atoms depending on how it was loaded.</summary>
public static class TextFile
{
    /// <summary>Applies the text-stream newline rule to already-read text:
    /// a CR-LF pair becomes <c>\n</c> where the platform calls for it. A
    /// LONE CR is left alone — only the pair is a line terminator.</summary>
    public static string NormalizeNewlines(string text) =>
        PositionTrackingReader.TranslateNewlinesByDefault && text.Contains('\r')
            ? text.Replace("\r\n", "\n")
            : text;

    /// <summary>Reads a Prolog source file as a text stream would deliver
    /// it: BOM-sniffed, then re-decoded per a leading <c>:- encoding/1</c>
    /// directive when one is present.</summary>
    public static string ReadAllText(string path, string defaultEncoding = "utf8") =>
        NormalizeNewlines(DecodeSource(System.IO.File.ReadAllBytes(path), defaultEncoding));

    /// <summary>Decodes source bytes honouring a BOM and a leading
    /// <c>:- encoding(E)</c> directive. Decoding is TOLERANT throughout —
    /// a file whose bytes are not valid under the default encoding but
    /// which carries the directive must not blow up before the directive
    /// is found: every attempted decode replaces ill-formed input instead
    /// of throwing, the directive is sniffed across the candidate
    /// decodings, and only then is the whole file re-decoded with the
    /// declared encoding.</summary>
    public static string DecodeSource(byte[] bytes, string defaultEncoding = "utf8")
    {
        var (bomName, bomLen) = TextEncodings.SniffBom(bytes, bytes.Length);
        string initial = bomName ?? defaultEncoding;
        string text = TextEncodings.DecodeLenient(initial, bytes, bomLen, bytes.Length - bomLen);

        // A BOM is authoritative (same precedence as open/4): the directive
        // is then at most a consistency statement, never a re-decode.
        if (bomName is not null) return text;

        string? declared = SniffEncodingDirective(text);
        if (declared is null)
        {
            // The prefix may be unreadable under UTF-8 (a UTF-16/32 file
            // with no BOM): probe the directive under each wide decoding.
            int probeLen = System.Math.Min(bytes.Length, 512);
            foreach (string cand in _wideCandidates)
            {
                declared = SniffEncodingDirective(
                    TextEncodings.DecodeLenient(cand, bytes, 0, probeLen));
                if (declared is not null) break;
            }
        }
        if (declared is null || declared == initial) return text;
        return TextEncodings.DecodeLenient(declared, bytes, 0, bytes.Length);
    }

    private static readonly string[] _wideCandidates =
        { "utf16le", "utf16be", "utf32le", "utf32be" };

    /// <summary>Textual sniff of a leading <c>:- encoding(Name)</c> —
    /// deliberately NOT the lexer (the surrounding text may be garbage under
    /// the attempted decoding; only the directive itself must be legible).
    /// Leading whitespace and %-comments may precede it, matching the "first
    /// term" rule loosely. Returns the ENGINE encoding name, or null.</summary>
    internal static string? SniffEncodingDirective(string text)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '%')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            break;
        }
        if (!MatchLiteral(text, ref i, ":-")) return null;
        SkipSpaces(text, ref i);
        if (!MatchLiteral(text, ref i, "encoding")) return null;
        SkipSpaces(text, ref i);
        if (i >= text.Length || text[i] != '(') return null;
        i++;
        SkipSpaces(text, ref i);
        bool quoted = i < text.Length && text[i] == '\'';
        if (quoted) i++;
        int start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i])
               || text[i] is '_' or '-')) i++;
        if (i == start) return null;
        string name = text[start..i];
        if (quoted)
        {
            if (i >= text.Length || text[i] != '\'') return null;
            i++;
        }
        SkipSpaces(text, ref i);
        if (i >= text.Length || text[i] != ')') return null;
        return TextEncodings.DirectiveNameToEngineName(name);
    }

    private static void SkipSpaces(string text, ref int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t') i++;
    }

    private static bool MatchLiteral(string text, ref int i, string lit)
    {
        if (i + lit.Length > text.Length) return false;
        for (int k = 0; k < lit.Length; k++)
            if (text[i + k] != lit[k]) return false;
        i += lit.Length;
        return true;
    }
}

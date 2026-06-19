using System.Globalization;
using System.Text;

namespace Shumway.Compiler.NativeC;

/// <summary>Token kinds for the embedded-C subset (ADR-022). Anything the lexer
/// does not recognise as a meaningful token becomes <see cref="Other"/> (a single
/// char) so the lenient declaration parser can skip noise from preprocessed
/// headers without the whole region failing.</summary>
public enum CTokenKind
{
    Ident, QuotedName, Int, String,
    Colon, Semicolon, Comma, Equals, Amp, Star,
    LParen, RParen, LBracket, RBracket, LBrace, RBrace,
    Other, Eof,
}

public readonly record struct CToken(CTokenKind Kind, string Text, long IntValue, int Offset)
{
    public override string ToString() => $"{Kind}('{Text}')@{Offset}";
}

/// <summary>Thrown by <see cref="CLexer"/> / the C parsers on malformed input.
/// The offset is into the raw C text of the region / block.</summary>
public sealed class CParseException(string message, int offset)
    : System.Exception(message)
{
    public int Offset { get; } = offset;
}

/// <summary>Tokenises the embedded-C subset. Skips whitespace, <c>//</c> and
/// <c>/* … */</c> comments, and <c>#…</c> preprocessor lines (e.g. the <c>#line</c>
/// / <c>#pragma</c> noise that fills preprocessed <c>:- c</c> regions). Function
/// names may be written Prolog-quoted (<c>'MakeCString'</c>) — captured as
/// <see cref="CTokenKind.QuotedName"/> with the quotes stripped.</summary>
public static class CLexer
{
    public static List<CToken> Tokenize(string source)
    {
        var tokens = new List<CToken>();
        int i = 0, n = source.Length;
        while (i < n)
        {
            char c = source[i];

            // Whitespace.
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Comments and preprocessor lines.
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                i += 2;
                while (i < n && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = System.Math.Min(i + 2, n);
                continue;
            }
            if (c == '#')   // preprocessor directive line (#line, #pragma, #define…)
            {
                while (i < n && source[i] != '\n') i++;
                continue;
            }
            if (c == '%')   // Arity allows a Prolog-style `%` line comment inside a
            {               // native block (used to comment out a native statement).
                while (i < n && source[i] != '\n') i++;
                continue;
            }

            // Identifier / keyword.
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                tokens.Add(new CToken(CTokenKind.Ident, source[s..i], 0, s));
                continue;
            }

            // Prolog-quoted name: 'MakeCString'  (a '' is a literal quote).
            if (c == '\'')
            {
                int s = i++;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (source[i] == '\'')
                    {
                        if (i + 1 < n && source[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++; break;
                    }
                    sb.Append(source[i++]);
                }
                tokens.Add(new CToken(CTokenKind.QuotedName, sb.ToString(), 0, s));
                continue;
            }

            // Integer literal (decimal or 0x… hex).
            if (char.IsDigit(c))
            {
                int s = i;
                long value;
                if (c == '0' && i + 1 < n && (source[i + 1] is 'x' or 'X'))
                {
                    i += 2;
                    int hs = i;
                    while (i < n && Uri.IsHexDigit(source[i])) i++;
                    value = long.Parse(source[hs..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
                else
                {
                    while (i < n && char.IsDigit(source[i])) i++;
                    value = long.Parse(source[s..i], CultureInfo.InvariantCulture);
                }
                // Trailing C integer suffix (L, U, UL…) — consume and ignore.
                while (i < n && (source[i] is 'L' or 'l' or 'U' or 'u')) i++;
                tokens.Add(new CToken(CTokenKind.Int, source[s..i], value, s));
                continue;
            }

            // String literal "…".
            if (c == '"')
            {
                int s = i++;
                var sb = new StringBuilder();
                while (i < n && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        char e = source[i + 1];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                        i += 2;
                        continue;
                    }
                    sb.Append(source[i++]);
                }
                if (i < n) i++;   // closing quote
                tokens.Add(new CToken(CTokenKind.String, sb.ToString(), 0, s));
                continue;
            }

            // Single-character punctuation.
            CTokenKind kind = c switch
            {
                ':' => CTokenKind.Colon,
                ';' => CTokenKind.Semicolon,
                ',' => CTokenKind.Comma,
                '=' => CTokenKind.Equals,
                '&' => CTokenKind.Amp,
                '*' => CTokenKind.Star,
                '(' => CTokenKind.LParen,
                ')' => CTokenKind.RParen,
                '[' => CTokenKind.LBracket,
                ']' => CTokenKind.RBracket,
                '{' => CTokenKind.LBrace,
                '}' => CTokenKind.RBrace,
                _ => CTokenKind.Other,
            };
            tokens.Add(new CToken(kind, c.ToString(), 0, i));
            i++;
        }
        tokens.Add(new CToken(CTokenKind.Eof, "", 0, n));
        return tokens;
    }
}

using System.Text;

namespace Shumway.Core;

/// <summary>How an atom's name relates to the BMP — computed once at intern
/// (every atom is born in <see cref="AtomTable"/>) so character-level
/// builtins can keep their O(1) UTF-16-unit fast paths for the
/// overwhelmingly common case and take a code-point walk only when they
/// must.</summary>
public enum TextShape : byte
{
    /// <summary>Every UTF-16 unit is its own code point — units ≡
    /// characters, all existing unit-based code is exact.</summary>
    Bmp = 0,

    /// <summary>Contains at least one well-formed surrogate pair (a code
    /// point above U+FFFF) and no lone surrogates: unit-based counting and
    /// slicing would lie, so character-level operations walk by code
    /// points.</summary>
    Astral = 1,

    /// <summary>Contains a lone surrogate — not a valid encoding of any
    /// character sequence. Such atoms can only be manufactured (a
    /// decomposition fragment, an embedding-supplied string); they unify
    /// and print as opaque values but have no defined character-level
    /// reading.</summary>
    Malformed = 2,
}

/// <summary>Code-point utilities over UTF-16 strings — the shared slow-path
/// toolkit for astral-aware character operations. Kept dependency-free (no
/// <c>System.Text.Rune</c>: absent on net48) and allocation-free except
/// where a string is the result.</summary>
public static class Utf16Text
{
    /// <summary>Single-pass classification; folded into atom interning.</summary>
    public static TextShape Classify(string s)
    {
        TextShape shape = TextShape.Bmp;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '\uD800' || c > '\uDFFF') continue;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length
                && char.IsLowSurrogate(s[i + 1]))
            {
                shape = TextShape.Astral;
                i++;
                continue;
            }
            return TextShape.Malformed;
        }
        return shape;
    }

    /// <summary>The number of code points in <paramref name="s"/> — the
    /// character-level length of an astral-bearing atom. O(units).</summary>
    public static int CodePointLength(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length
                && char.IsLowSurrogate(s[i + 1]))
                i++;
            n++;
        }
        return n;
    }

    /// <summary>The code point starting at unit index <paramref name="i"/>,
    /// advancing <paramref name="i"/> past it (by 1 or 2 units). A lone
    /// surrogate is returned as its own unit value — malformed input reads
    /// unit-wise rather than throwing, matching how such atoms already
    /// behave everywhere else.</summary>
    public static int CodePointAt(string s, ref int i)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length
            && char.IsLowSurrogate(s[i + 1]))
        {
            int cp = char.ConvertToUtf32(c, s[i + 1]);
            i += 2;
            return cp;
        }
        i += 1;
        return c;
    }

    /// <summary>Unit index of the <paramref name="cpIndex"/>-th code point
    /// (clamped to <c>s.Length</c> when past the end). O(units).</summary>
    public static int UnitIndexOf(string s, int cpIndex)
    {
        int i = 0;
        while (cpIndex > 0 && i < s.Length)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length
                && char.IsLowSurrogate(s[i + 1]))
                i++;
            i++;
            cpIndex--;
        }
        return i;
    }

    /// <summary>Appends one code point (1 or 2 units).</summary>
    public static void AppendCodePoint(StringBuilder sb, int cp)
    {
        if (cp <= 0xFFFF) sb.Append((char)cp);
        else
        {
            cp -= 0x10000;
            sb.Append((char)(0xD800 + (cp >> 10)));
            sb.Append((char)(0xDC00 + (cp & 0x3FF)));
        }
    }

    /// <summary>Unit index of each code point start, plus the final length —
    /// the boundary table that lets a code-point-indexed operation over an
    /// astral-bearing string still slice in O(1) after one O(n) pass.</summary>
    public static int[] CpBounds(string s)
    {
        var bl = new System.Collections.Generic.List<int>(s.Length + 1);
        for (int u = 0; u < s.Length; )
        {
            bl.Add(u);
            CodePointAt(s, ref u);
        }
        bl.Add(s.Length);
        return bl.ToArray();
    }

    /// <summary>A valid Unicode scalar value — what a Prolog character
    /// code may denote: 0..10FFFF excluding the surrogate range.</summary>
    public static bool IsScalarValue(long cp)
        => (ulong)cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF);

    /// <summary>True when <paramref name="s"/> is exactly ONE code point —
    /// the shape of a Prolog character atom (one unit, or one well-formed
    /// surrogate pair).</summary>
    public static bool IsOneCodePoint(string s)
        => s.Length == 1
           || (s.Length == 2 && char.IsHighSurrogate(s[0])
               && char.IsLowSurrogate(s[1]));

    /// <summary>The code point of a one-code-point string (see
    /// <see cref="IsOneCodePoint"/>).</summary>
    public static int SingleCodePoint(string s)
        => s.Length == 1 ? s[0] : char.ConvertToUtf32(s[0], s[1]);

    /// <summary>The string of one code point.</summary>
    public static string FromCodePoint(int cp)
        => cp <= 0xFFFF ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);

    /// <summary>Ordinal comparison in CODE POINT order. Unit-wise ordinal
    /// order differs exactly when one side has a surrogate (D800–DFFF) and
    /// the other a unit in E000–FFFF — the astral character would sort
    /// below. The standard fix-up: when the differing units both lie at or
    /// above D800, remap E000–FFFF down and surrogates up before
    /// comparing.</summary>
    public static int CompareCodePointOrder(string a, string b)
    {
        int n = a.Length < b.Length ? a.Length : b.Length;
        for (int i = 0; i < n; i++)
        {
            char ca = a[i], cb = b[i];
            if (ca == cb) continue;
            if (ca >= '\uD800' && cb >= '\uD800')
            {
                int fa = ca >= 0xE000 ? ca - 0x800 : ca + 0x2000;
                int fb = cb >= 0xE000 ? cb - 0x800 : cb + 0x2000;
                return fa - fb;
            }
            return ca - cb;
        }
        return a.Length - b.Length;
    }
}

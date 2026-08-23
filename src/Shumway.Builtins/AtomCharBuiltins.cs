using System.Globalization;
using System.Text;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Atom / char / number bridging builtins:
/// <list type="bullet">
/// <item><c>atom_length/2</c>: code-unit length of an atom's name.</item>
/// <item><c>atom_chars/2</c>: atom ↔ list of single-character atoms.</item>
/// <item><c>char_code/2</c>: single-character atom ↔ integer code.</item>
/// <item><c>number_codes/2</c>: integer/float ↔ list of integer codes (the
///   decimal text representation).</item>
/// <item><c>number_chars/2</c>: integer/float ↔ list of single-character
///   atoms.</item>
/// </list>
///
/// <para>Every contract-violation throw is a catchable
/// <see cref="PrologRuntimeException"/> with an ISO-shaped kind. Argument
/// checks honour the ISO precedence from §7.12.2 — instantiation_error
/// before type_error before representation_error.</para>
/// </summary>
public static class AtomCharBuiltins
{
    // ---------- atom_length/2 ----------

    public static bool AtomLength(Activation engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        // ISO §8.16.1.3: Atom var → instantiation_error; not atom →
        // type_error(atom, Atom); a bound Length must be a non-negative
        // integer (checked whatever the atom looks like).
        if (atomCell.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        Cell lenCell = Resolve(engine, engine.GetRegister(1));
        if (lenCell.Tag is not (Tag.Ref or Tag.AttVar))
        {
            if (lenCell.Tag != Tag.Int)
                throw new PrologRuntimeException(
                    "type_error", "integer", engine, lenCell);
            if (lenCell.AsInt < 0)
                throw new PrologRuntimeException(
                    "domain_error", "not_less_than_zero", engine, lenCell);
        }
        if (atomCell.Tag != Tag.Atom)
        {
            // SWI accepts any atomic (a number/string) and returns the length of
            // its text; ISO raises type_error(atom). Only for an SWI caller.
            if (SwiLenient.TryCoerce(engine, atomCell, out string coerced))
                return engine.UnifyRegisterWithCell(1, Cell.Int(coerced.Length));
            throw new PrologRuntimeException("type_error", "atom", engine, atomCell);
        }
        string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        return engine.UnifyRegisterWithCell(1, Cell.Int(name.Length));
    }

    // ---------- atom_string/2 ----------

    /// <summary><c>atom_string(Atom, String)</c> — bidirectional atom
    /// ↔ PSTR conversion. Either argument may be ground; given an atom
    /// the PSTR is built from the atom's name, and given a PSTR the
    /// atom is interned from its characters.</summary>
    public static bool AtomString(Activation engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        if (atomCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            int pstrIdx = engine.MakePstr(name, TextKind.Chars);
            return engine.UnifyRegisterWithCell(1, Cell.Ref(pstrIdx));
        }

        Cell strCell = Resolve(engine, engine.GetRegister(1));
        if (strCell.Tag == Tag.Pstr)
        {
            string name = engine.AsPstrString(engine.Deref(engine.GetRegister(1).AsHeapIndex));
            int atomId = AtomTable.Intern(name, permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }

        // Neither side is sufficiently instantiated: instantiation_error
        // (both args var) or type_error(atom|string) depending on which
        // is at the wrong type. We pick instantiation_error when at least
        // one is var — the conservative ISO-style report.
        if (atomCell.Tag is Tag.Ref or Tag.AttVar || strCell.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        throw new PrologRuntimeException("type_error", "atom");
    }

    // ---------- atom_chars/2 ----------

    public static bool AtomChars(Activation engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        Cell charsCell = Resolve(engine, engine.GetRegister(1));

        if (atomCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            // Both bound: the char list is still type-checked (§8.16.4.3).
            // Only a PROPER list is validated-and-compared: a partial
            // one (atom_codes(abc, [0'a|T])) must still unify.
            if (ListCursor.IsProperListCell(engine, charsCell))
                return ReadCharAtomsToString(engine, charsCell) == name;
            int listIdx = BuildCharAtomList(engine, name);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }

        if (atomCell.Tag is Tag.Ref or Tag.AttVar)
        {
            // Read direction: Atom is var, so Chars must be a proper list
            // of single-character atoms. If it isn't, ReadCharAtomsToString
            // raises the appropriate type_error / instantiation_error.
            if (charsCell.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            string name = ReadCharAtomsToString(engine, charsCell);
            int atomId = AtomTable.Intern(name, permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }

        // Atom is bound but to something other than an atom — ISO §8.16.4.
        // SWI coerces a number/string to its text and yields its chars.
        if (SwiLenient.TryCoerce(engine, atomCell, out string coercedChars))
            return engine.UnifyRegisterWithHeapAt(1, BuildCharAtomList(engine, coercedChars));
        throw new PrologRuntimeException("type_error", "atom", engine, atomCell);
    }

    /// <summary><c>'$sub_atom_icasechk'(+Haystack, ?Before, +Needle)</c> — the C#
    /// helper behind the SWI shim's <c>sub_atom_icasechk/3</c>: deterministically
    /// finds the first case-insensitive occurrence of Needle in Haystack and
    /// unifies Before with its 0-based offset; fails if absent.</summary>
    public static bool SubAtomICaseChk(Activation engine)
    {
        Cell h = Resolve(engine, engine.GetRegister(0));
        Cell nCell = Resolve(engine, engine.GetRegister(2));
        if (h.Tag is Tag.Ref or Tag.AttVar || nCell.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        if (h.Tag != Tag.Atom || nCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        string haystack = AtomTable.GetById(h.AsAtomId)?.Name ?? "";
        string needle = AtomTable.GetById(nCell.AsAtomId)?.Name ?? "";
        int idx = haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        return engine.UnifyRegisterWithCell(1, Cell.Int(idx));
    }

    // ---------- char_code/2 ----------

    public static bool CharCode(Activation engine)
    {
        Cell charCell = Resolve(engine, engine.GetRegister(0));
        Cell codeCell = Resolve(engine, engine.GetRegister(1));
        // §8.16.5.3.c: a BOUND non-integer Code is type_error(integer, C),
        // checked even when Char is a usable character.
        if (codeCell.Tag is not (Tag.Ref or Tag.AttVar) && codeCell.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer", engine, codeCell);

        if (charCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(charCell.AsAtomId)?.Name ?? "";
            // ISO §8.16.5: a non-character first arg is type_error(character).
            if (name.Length != 1)
                throw new PrologRuntimeException(
                    "type_error", "character", engine, charCell);
            return engine.UnifyRegisterWithCell(1, Cell.Int(name[0]));
        }

        if (codeCell.Tag == Tag.Int)
        {
            long code = codeCell.AsInt;
            // ISO §8.16.5.3.f: a code outside the implementation-defined
            // character set is representation_error(character_code).
            if (code < 0 || code > char.MaxValue)
                throw new PrologRuntimeException("representation_error", "character_code");
            // Cached single-char atom ids: no lock, no allocation for the
            // common range; higher BMP code points fall back to Intern.
            int cached = AtomTable.GetSingleCharAtomId((int)code);
            int atomId = cached >= 0
                ? cached
                : AtomTable.Intern(((char)code).ToString(), permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }

        // ISO §8.16.5.3.a-b: both var → instantiation_error; otherwise
        // one is bound but to the wrong type.
        if (charCell.Tag is Tag.Ref or Tag.AttVar && codeCell.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        // Bound to something other than atom / int: report the offending
        // argument's expected type AND value. Char takes precedence when
        // both are bound (a non-atom Char is what ISO checks first).
        if (charCell.Tag is not (Tag.Ref or Tag.AttVar))
            throw new PrologRuntimeException(
                "type_error", "character", engine, charCell);
        throw new PrologRuntimeException(
            "type_error", "integer", engine, codeCell);
    }

    // ---------- number_codes/2 ----------

    public static bool NumberCodes(Activation engine) => NumberConversion(
        engine, asCodes: true, builtinName: "number_codes/2");

    // ---------- number_chars/2 ----------

    public static bool NumberChars(Activation engine) => NumberConversion(
        engine, asCodes: false, builtinName: "number_chars/2");

    private static bool NumberConversion(Activation engine, bool asCodes, string builtinName)
    {
        Cell numCell = Resolve(engine, engine.GetRegister(0));
        Cell strCell = Resolve(engine, engine.GetRegister(1));
        bool numIsNumber = numCell.Tag is Tag.Int or Tag.Float or Tag.BigInt or Tag.Rational;

        // ISO §8.16.7 / §8.16.8 precedence and direction.
        //  (a) both arguments unbound → instantiation_error.
        if (numCell.Tag is Tag.Ref or Tag.AttVar && strCell.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        //  (b) Number bound to a non-number → type_error(number, _).
        if (numCell.Tag is not (Tag.Ref or Tag.AttVar) && !numIsNumber)
            throw new PrologRuntimeException("type_error", "number", engine, numCell);

        //  (c) The list argument, when instantiated, is type-checked and — if
        //      fully bound — parsed as a number and unified with Number (the
        //      PRIMARY direction, so number_chars(1, "01") parses "01"→1 and
        //      succeeds). A bound element that is not a character is a
        //      type_error even past an earlier unbound one (§8.16.8); a list
        //      that only has unbound elements/tail falls through to generate.
        if (strCell.Tag is not (Tag.Ref or Tag.AttVar))
        {
            string s = AnalyzeCharList(engine, strCell, asCodes, numIsNumber, out bool hasUnbound);
            if (!hasUnbound)
            {
                // Full Prolog number syntax (§6.4.4): radix (0x/0o/0b), char
                // code (0'c), BigInteger-size decimals, floats, leading layout.
                if (TryBuildPrologNumber(engine, s, out Cell numResult, out int floatIdx))
                    return floatIdx >= 0
                        ? engine.UnifyRegisterWithHeapAt(0, floatIdx)
                        : engine.UnifyRegisterWithCell(0, numResult);
                // ISO §8.16.8 reads the chars as a TERM that must be a number, so
                // `'-'1` (quoted prefix minus) is -1 and `'\n-' 3` is -3 — cases the
                // token parser above doesn't cover. Fall back to the host's full
                // term reader (wired on the engine); a non-number result stays a
                // syntax error.
                if (engine.NumberFromChars?.Invoke(s) is { } boxed)
                {
                    if (boxed is double d)
                        return engine.UnifyRegisterWithHeapAt(0, engine.MakeFloat(d));
                    System.Numerics.BigInteger bi = boxed switch
                    {
                        long l => l,
                        System.Numerics.BigInteger b => b,
                        int ii => ii,
                        _ => throw new PrologRuntimeException("syntax_error", "illegal_number"),
                    };
                    Cell c = default;
                    FinishInteger(engine, bi, ref c);
                    return engine.UnifyRegisterWithCell(0, c);
                }
                // Text that does not denote a number is a (catchable) syntax error.
                throw new PrologRuntimeException("syntax_error", "illegal_number");
            }
            // The list has unbound parts: Number must supply the value.
            if (!numIsNumber)
                throw new PrologRuntimeException("instantiation_error");
            // fall through to generate + unify against the partial list.
        }

        //  (d) The list is a variable (or a partial list) and Number is a
        //      number → generate its characters and unify.
        string text = numCell.Tag switch
        {
            Tag.Int => numCell.AsInt.ToString(CultureInfo.InvariantCulture),
            Tag.Float => Number.FormatPrologFloat(
                Cell.DecodeFloat(numCell, engine.GetHeap(numCell.FloatPairedIndex))),
            _ => engine.AsBigInt(numCell).ToString(CultureInfo.InvariantCulture),
        };
        int listIdx = asCodes
            ? BuildIntCodesList(engine, text)
            : BuildCharAtomList(engine, text);
        return engine.UnifyRegisterWithHeapAt(1, listIdx);
    }

    /// <summary>Parses ISO Prolog number syntax
    /// (§6.4.4): optional leading layout, optional sign, then a decimal
    /// integer (any size — BigInteger past long), a radix literal
    /// (<c>0x</c>/<c>0o</c>/<c>0b</c>), a character-code literal
    /// (<c>0'c</c>, with the quoted-char escapes), or a float
    /// (<c>3.14</c>, <c>1.0e-5</c>, and the widely-accepted <c>1e5</c>).
    /// On success either <paramref name="cell"/> holds an Int/BigInt cell
    /// (<paramref name="floatIdx"/> = −1) or <paramref name="floatIdx"/>
    /// is the heap index of a materialised float. Returns false when the
    /// text is not a number (caller raises
    /// <c>syntax_error(illegal_number)</c>).</summary>
    internal static bool TryBuildPrologNumber(
        Activation engine, string s, out Cell cell, out int floatIdx)
    {
        cell = default;
        floatIdx = -1;
        int i = 0, n = s.Length;
        // Leading layout — whitespace AND comments (§6.4.1), so a number token
        // may be preceded by `/* */` or a `%` line comment.
        i = SkipLayout(s, i);
        // ISO §6.3.1: a numeric constant has no leading '+' — only a '-' sign
        // (which yields a negative number). Layout after the sign includes
        // comments — but ONLY after real WHITESPACE: `- /**/1` is -1 (Neumerkel
        // number_chars_cont row 40) exactly as `- 1` is (row 36). A comment glued
        // to the sign (`-/**/1`, row 41) is NOT layout — `-/**/` is all graphic
        // chars, one atom — so it stays a syntax error; `-1` (digit glued) is a
        // literal.
        bool neg = false;
        if (i < n && s[i] == '-')
        {
            neg = true;
            i++;
            if (i < n && char.IsWhiteSpace(s[i]))
                i = SkipLayout(s, i);   // "- 1" or "- /**/1" → -1
        }
        if (i >= n || !char.IsDigit(s[i])) return false;

        // Radix / char-code literals — all start "0<marker>".
        if (s[i] == '0' && i + 1 < n)
        {
            char marker = s[i + 1];
            if (marker == 'x' || marker == 'o' || marker == 'b')
            {
                int radix = marker == 'x' ? 16 : marker == 'o' ? 8 : 2;
                int j = i + 2;
                System.Numerics.BigInteger acc = 0;
                int digits = 0;
                while (j < n)
                {
                    int d = RadixDigitValue(s[j], radix);
                    if (d < 0) break;
                    acc = acc * radix + d;
                    digits++; j++;
                }
                if (digits == 0 || j != n) return false;
                return FinishInteger(engine, neg ? -acc : acc, ref cell);
            }
            if (marker == '\'')
            {
                if (!TryParseCharCodeLiteral(s, i + 2, out long code)) return false;
                cell = Cell.Int(neg ? -code : code);
                return true;
            }
        }

        // Decimal integer / float.
        int start = i;
        while (i < n && char.IsDigit(s[i])) i++;
        if (i == n)
        {
            var acc = System.Numerics.BigInteger.Parse(
                s.Substring(start), CultureInfo.InvariantCulture);
            return FinishInteger(engine, neg ? -acc : acc, ref cell);
        }
        // Float: fraction and/or exponent after the integer part.
        bool sawFraction = false;
        if (s[i] == '.' && i + 1 < n && char.IsDigit(s[i + 1]))
        {
            sawFraction = true;
            i += 2;
            while (i < n && char.IsDigit(s[i])) i++;
        }
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
            if (j < n && char.IsDigit(s[j]))
            {
                i = j + 1;
                while (i < n && char.IsDigit(s[i])) i++;
            }
        }
        // ISO §6.3.1.2: a float MUST have a fractional part (a decimal point
        // followed by digits); an exponent alone (1e1) is not a valid float.
        if (!sawFraction) return false;
        if (i != n) return false;
        if (!double.TryParse(s.Substring(start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double dv))
            return false;
        floatIdx = engine.MakeFloat(neg ? -dv : dv);
        return true;
    }

    private static bool FinishInteger(
        Activation engine, System.Numerics.BigInteger value, ref Cell cell)
    {
        cell = value >= long.MinValue && value <= long.MaxValue
            ? Cell.Int((long)value)
            : engine.MakeBigInt(value);
        return true;
    }

    private static int RadixDigitValue(char c, int radix)
    {
        int v = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return v >= 0 && v < radix ? v : -1;
    }

    /// <summary>Parses the character part of a <c>0'…</c> literal
    /// starting at <paramref name="i"/>; the literal must consume the
    /// whole remaining string. Handles <c>0'''</c> (quoted quote), the
    /// single-char control escapes, and <c>\x…\</c> / octal
    /// <c>\…\</c>.</summary>
    /// <summary>Skips ISO layout (§6.4.1) — whitespace, <c>%</c> line comments
    /// and <c>/* */</c> block comments — from <paramref name="i"/>, returning the
    /// index of the first non-layout character. Shared by the number parser's
    /// leading-layout and post-sign-layout skips.</summary>
    private static int SkipLayout(string s, int i)
    {
        int n = s.Length;
        while (i < n)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '%') { while (i < n && s[i] != '\n') i++; continue; }
            if (s[i] == '/' && i + 1 < n && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i = System.Math.Min(n, i + 2);
                continue;
            }
            break;
        }
        return i;
    }

    private static bool TryParseCharCodeLiteral(string s, int i, out long code)
    {
        code = 0;
        int n = s.Length;
        if (i >= n) return false;
        char c = s[i];
        if (c == '\'')
        {
            // 0''' — the quote char, written doubled.
            if (i + 1 < n && s[i + 1] == '\'' && i + 2 == n) { code = '\''; return true; }
            return false;
        }
        if (c != '\\')
        {
            if (i + 1 != n) return false;
            // ISO §6.3.7 / §6.4.2.1: a raw control character (newline, tab, …)
            // is not a valid char in a 0'c literal — it must be escaped
            // (0'\n). Reject it so `number_chars(N, "0'<newline>")` is a
            // syntax error, mirroring the lexer's quoted-atom rule.
            if (char.IsControl(c)) return false;
            code = c;
            return true;
        }
        // Backslash escape.
        i++;
        if (i >= n) return false;
        char e = s[i];
        switch (e)
        {
            case 'n': code = '\n'; return i + 1 == n;
            case 't': code = '\t'; return i + 1 == n;
            case 'r': code = '\r'; return i + 1 == n;
            case 'a': code = 7;    return i + 1 == n;
            case 'b': code = 8;    return i + 1 == n;
            case 'f': code = 12;   return i + 1 == n;
            case 'v': code = 11;   return i + 1 == n;
            case '\\': code = '\\'; return i + 1 == n;
            case '\'': code = '\''; return i + 1 == n;
            case '"': code = '"';  return i + 1 == n;
            case '`': code = '`';  return i + 1 == n;
            case 'x':
            {
                // \xHH…\ — hex code terminated by a backslash.
                long acc = 0; int j = i + 1; int digits = 0;
                while (j < n && RadixDigitValue(s[j], 16) >= 0)
                {
                    acc = acc * 16 + RadixDigitValue(s[j], 16);
                    digits++; j++;
                }
                if (digits == 0 || j >= n || s[j] != '\\' || j + 1 != n) return false;
                code = acc;
                return true;
            }
            case >= '0' and <= '7':
            {
                // \NNN\ — octal code terminated by a backslash.
                long acc = 0; int j = i; int digits = 0;
                while (j < n && s[j] >= '0' && s[j] <= '7')
                {
                    acc = acc * 8 + (s[j] - '0');
                    digits++; j++;
                }
                if (digits == 0 || j >= n || s[j] != '\\' || j + 1 != n) return false;
                code = acc;
                return true;
            }
            default: return false;
        }
    }

    // ---------- unicode_property/2 ----------

    /// <summary><c>unicode_property(+Code, ?Property)</c> — the subset of
    /// SWI's <c>library(unicode)</c> the Logtalk libraries use (json_path's
    /// iregexp <c>\p{...}</c> filters, library(types)): <c>category(C)</c>
    /// with C the two-letter Unicode general category, exact from the .NET
    /// Unicode tables rather than approximated over char_type.</summary>
    public static bool UnicodeProperty(Activation engine)
    {
        Cell codeCell = Resolve(engine, engine.GetRegister(0));
        if (codeCell.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        if (codeCell.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer");
        long code = codeCell.AsInt;
        if (code < 0 || code > 0x10FFFF)
            throw new PrologRuntimeException(
                "representation_error", "character_code");

        string category = char.ConvertFromUtf32((int)code) is { } s && s.Length > 0
            ? CategoryCode(CharUnicodeInfo.GetUnicodeCategory(s, 0))
            : "Cn";

        // category(C) — built on the heap and unified with the Property arg,
        // so a bound category('Nd') acts as a test and a variable receives it.
        int catAtom = AtomTable.Intern(category, permanent: true).Id;
        int fid = FunctorTable.Intern(
            AtomTable.Intern("category", permanent: true).Id, 1);
        int idx = engine.AllocateHeap(2);
        engine.SetHeap(idx, Cell.Functor(fid));
        engine.SetHeap(idx + 1, Cell.Atom(catAtom));
        return engine.UnifyRegisterWithCell(1, Cell.Str(idx));
    }

    /// <summary>The standard two-letter spelling of each .NET
    /// <see cref="UnicodeCategory"/> value.</summary>
    private static string CategoryCode(UnicodeCategory c) => c switch
    {
        UnicodeCategory.UppercaseLetter => "Lu",
        UnicodeCategory.LowercaseLetter => "Ll",
        UnicodeCategory.TitlecaseLetter => "Lt",
        UnicodeCategory.ModifierLetter => "Lm",
        UnicodeCategory.OtherLetter => "Lo",
        UnicodeCategory.NonSpacingMark => "Mn",
        UnicodeCategory.SpacingCombiningMark => "Mc",
        UnicodeCategory.EnclosingMark => "Me",
        UnicodeCategory.DecimalDigitNumber => "Nd",
        UnicodeCategory.LetterNumber => "Nl",
        UnicodeCategory.OtherNumber => "No",
        UnicodeCategory.SpaceSeparator => "Zs",
        UnicodeCategory.LineSeparator => "Zl",
        UnicodeCategory.ParagraphSeparator => "Zp",
        UnicodeCategory.Control => "Cc",
        UnicodeCategory.Format => "Cf",
        UnicodeCategory.Surrogate => "Cs",
        UnicodeCategory.PrivateUse => "Co",
        UnicodeCategory.ConnectorPunctuation => "Pc",
        UnicodeCategory.DashPunctuation => "Pd",
        UnicodeCategory.OpenPunctuation => "Ps",
        UnicodeCategory.ClosePunctuation => "Pe",
        UnicodeCategory.InitialQuotePunctuation => "Pi",
        UnicodeCategory.FinalQuotePunctuation => "Pf",
        UnicodeCategory.OtherPunctuation => "Po",
        UnicodeCategory.MathSymbol => "Sm",
        UnicodeCategory.CurrencySymbol => "Sc",
        UnicodeCategory.ModifierSymbol => "Sk",
        UnicodeCategory.OtherSymbol => "So",
        _ => "Cn",
    };

    // ---------- atom_number/2 ----------

    /// <summary><c>atom_number(?Atom, ?Number)</c> — converts between an
    /// atom and the number it denotes. Unlike <c>number_codes/2</c> this
    /// <em>fails</em> (rather than raising a syntax error) when the atom is
    /// not numeric, matching the conventional <c>atom_number/2</c>.</summary>
    public static bool AtomNumber(Activation engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        if (atomCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            // Full Prolog number syntax (radix, 0'c, BigInteger, floats),
            // same parser as number_codes/2.
            if (TryBuildPrologNumber(engine, name, out Cell numCell2, out int fIdx))
                return fIdx >= 0
                    ? engine.UnifyRegisterWithHeapAt(1, fIdx)
                    : engine.UnifyRegisterWithCell(1, numCell2);
            return false;   // not numeric — fail, do not throw
        }

        Cell numCell = Resolve(engine, engine.GetRegister(1));
        string? text = NumberText(engine, numCell);
        if (text is null)
        {
            // Atom is non-atom (or both var) — sort the error.
            if (atomCell.Tag is Tag.Ref or Tag.AttVar && numCell.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            if (atomCell.Tag is not (Tag.Ref or Tag.AttVar) && atomCell.Tag != Tag.Atom)
                throw new PrologRuntimeException("type_error", "atom");
            throw new PrologRuntimeException("type_error", "number");
        }
        return engine.UnifyRegisterWithCell(
            0, Cell.Atom(AtomTable.Intern(text, permanent: false).Id));
    }

    // ---------- number_string/2 ----------

    /// <summary><c>number_string(?Number, ?String)</c> — converts between a
    /// number and its string representation, failing when the string is not
    /// numeric.</summary>
    public static bool NumberString(Activation engine)
    {
        Cell strCell = Resolve(engine, engine.GetRegister(1));
        if (strCell.Tag == Tag.Pstr)
        {
            string s = engine.AsPstrString(
                engine.Deref(engine.GetRegister(1).AsHeapIndex));
            if (long.TryParse(s, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long iv))
                return engine.UnifyRegisterWithCell(0, Cell.Int(iv));
            if (double.TryParse(s, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double dv))
                return engine.UnifyRegisterWithHeapAt(0, engine.MakeFloat(dv));
            return false;   // not numeric — fail, do not throw
        }

        Cell numCell = Resolve(engine, engine.GetRegister(0));
        string? text = NumberText(engine, numCell);
        if (text is null)
        {
            if (numCell.Tag is Tag.Ref or Tag.AttVar && strCell.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            if (strCell.Tag is not (Tag.Ref or Tag.AttVar) && strCell.Tag != Tag.Pstr)
                throw new PrologRuntimeException("type_error", "string");
            throw new PrologRuntimeException("type_error", "number");
        }
        return engine.UnifyRegisterWithCell(1, Cell.Ref(engine.MakePstr(text, TextKind.Chars)));
    }

    /// <summary>The decimal text of an integer or float cell; null when the
    /// cell is neither.</summary>
    private static string? NumberText(Activation engine, Cell numCell)
    {
        if (numCell.Tag == Tag.Int)
            return numCell.AsInt.ToString(CultureInfo.InvariantCulture);
        if (numCell.Tag == Tag.Float)
        {
            double v = Cell.DecodeFloat(numCell, engine.GetHeap(numCell.FloatPairedIndex));
            return Number.FormatPrologFloat(v);
        }
        return null;
    }

    /// <summary><c>sub_atom(Atom, Before, Length, After, SubAtom)</c> —
    /// deterministic substring extraction. Supported modes:
    /// <list type="bullet">
    /// <item><c>(+, +, +, ?, ?)</c>: extract the substring at the given
    ///   offset and length.</item>
    /// <item><c>(+, ?, ?, ?, +)</c>: find the first occurrence of
    ///   <c>SubAtom</c> in <c>Atom</c> and bind the three index
    ///   variables accordingly.</item>
    /// </list>
    /// Not currently registered: <c>sub_atom/5</c> is provided by the
    /// prelude over the backtrackable <c>$sub_atom_enum</c>; this
    /// deterministic variant covers only the modes above.</summary>
    public static bool SubAtom(Activation engine)
    {
        Cell atomC = Resolve(engine, engine.GetRegister(0));
        // ISO §8.16.10.3: Atom var → instantiation_error; not atom → type_error.
        if (atomC.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        string atomName;
        if (atomC.Tag == Tag.Atom) atomName = AtomTable.GetById(atomC.AsAtomId)?.Name ?? "";
        else if (!SwiLenient.TryCoerce(engine, atomC, out atomName))   // SWI: accept atomic
            throw new PrologRuntimeException("type_error", "atom");

        Cell beforeC = Resolve(engine, engine.GetRegister(1));
        Cell lengthC = Resolve(engine, engine.GetRegister(2));
        Cell subC    = Resolve(engine, engine.GetRegister(4));

        // Mode 1: Before + Length both ground integers.
        if (beforeC.Tag == Tag.Int && lengthC.Tag == Tag.Int)
        {
            int before = (int)beforeC.AsInt;
            int length = (int)lengthC.AsInt;
            if (before < 0 || length < 0 || before + length > atomName.Length)
                return false;
            int after = atomName.Length - before - length;
            string sub = atomName.Substring(before, length);
            int subAtomId = AtomTable.Intern(sub, permanent: false).Id;
            if (!engine.UnifyRegisterWithCell(3, Cell.Int(after))) return false;
            if (!engine.UnifyRegisterWithCell(4, Cell.Atom(subAtomId))) return false;
            return true;
        }

        // Mode 2: SubAtom ground — find first occurrence.
        if (subC.Tag == Tag.Atom)
        {
            string sub = AtomTable.GetById(subC.AsAtomId)?.Name ?? "";
            int idx = atomName.IndexOf(sub, StringComparison.Ordinal);
            if (idx < 0) return false;
            int after = atomName.Length - idx - sub.Length;
            if (!engine.UnifyRegisterWithCell(1, Cell.Int(idx))) return false;
            if (!engine.UnifyRegisterWithCell(2, Cell.Int(sub.Length))) return false;
            if (!engine.UnifyRegisterWithCell(3, Cell.Int(after))) return false;
            return true;
        }

        // Unsupported modes bottom out here; the ISO-appropriate
        // diagnostic is instantiation_error (the missing args ARE the
        // problem).
        throw new PrologRuntimeException("instantiation_error");
    }

    // ---------- List-building helpers ----------

    private static int BuildIntCodesList(Activation engine, string s)
        => engine.MakeTextList(s, TextKind.Codes);

    private static int BuildCharAtomList(Activation engine, string s)
        => engine.MakeTextList(s, TextKind.Chars);

    // ---------- List-reading helpers ----------

    /// <summary>Walks a number_chars/number_codes list argument (§8.16.8),
    /// concatenating its bound characters. Sets <paramref name="hasUnbound"/>
    /// when any element or the tail is an unbound variable — the caller then
    /// takes the generate direction (Number → list) rather than parsing.
    /// A bound element that is not a valid character / code, or a bound
    /// improper tail, throws a type_error even past an earlier unbound
    /// element (a bad bound element wins over instantiation). The returned
    /// string is meaningful only when <paramref name="hasUnbound"/> is
    /// false.</summary>
    private static string AnalyzeCharList(
        Activation engine, Cell listCell, bool asCodes, bool numberBound, out bool hasUnbound)
    {
        hasUnbound = false;
        var sb = new StringBuilder();
        Cell cursor = ListCursor.Resolve(engine, listCell);
        // A packed run whose presentation MATCHES what this call wants is
        // consumed in bulk; one that does not falls through to the element
        // loop, which raises the ISO element error from the element's own tag
        // rather than from an assumption about the representation (ADR-047).
        if (cursor.Tag == Tag.Pstr && cursor.AsPstrLength > 0
            && cursor.AsPstrKind == (asCodes ? TextKind.Codes : TextKind.Chars))
        {
            sb.Append(engine.ReadPstrChain(cursor, out cursor));
            cursor = ListCursor.Resolve(engine, cursor);
        }
        // ISO §8.16.8.3.a — when Number is a VARIABLE, a partial list (unbound
        // tail) is instantiation_error, which takes precedence over a type_error
        // on any element. Detect it by walking the spine first, so
        // `number_chars(N, [1|_])` reports instantiation_error (via the
        // hasUnbound path the caller checks) rather than type_error(character) on
        // the head 1. Gated on !numberBound: when Number is BOUND we are
        // generating + unifying, so a partial list is fine but a bad element
        // (`number_chars(1, [[]|_])`) must still type_error — the element walk
        // below does that. A proper list always gets the element checks; an
        // improper (non-list) tail falls through to type_error(list) below.
        if (!numberBound)
        {
            Cell spine = cursor;
            int spineSteps = 0, spineCap = engine.HeapTop + 1;
            while (ListCursor.TryUncons(engine, spine, out _, out Cell spineTail))
            {
                if (++spineSteps > spineCap)
                    throw new PrologRuntimeException("type_error", "list");
                spine = ListCursor.Resolve(engine, spineTail);
            }
            if (spine.Tag is Tag.Ref or Tag.AttVar || spine.Tag == Tag.AttVar)
            {
                hasUnbound = true;
                return sb.ToString();
            }
        }
        // A proper list has at most one cons cell per heap cell; walking more
        // than that means the list is cyclic (e.g. L = ['1'|L]). Bound the walk
        // so a cyclic argument raises type_error(list) instead of looping until
        // the process runs out of memory (an uncatchable .NET failure).
        int steps = 0, stepCap = engine.HeapTop + 1;
        while (ListCursor.TryUncons(engine, cursor, out Cell rawHead, out Cell aTail))
        {
            if (++steps > stepCap)
                throw new PrologRuntimeException("type_error", "list");
            Cell head = Resolve(engine, rawHead);
            if (head.Tag is Tag.Ref or Tag.AttVar)
            {
                hasUnbound = true;
            }
            else if (asCodes)
            {
                if (head.Tag != Tag.Int)
                    throw new PrologRuntimeException(
                        "type_error", "integer", engine, head);
                // BMP-only, same contract as char_code/2: silently casting
                // would BUILD A DIFFERENT CHARACTER (0x10400 → 0x400).
                if (head.AsInt < 0 || head.AsInt > char.MaxValue)
                    throw new PrologRuntimeException(
                        "representation_error", "character_code");
                if (!hasUnbound) sb.Append((char)head.AsInt);
            }
            else
            {
                if (head.Tag != Tag.Atom)
                    throw new PrologRuntimeException(
                        "type_error", "character", engine, head);
                string name = AtomTable.GetById(head.AsAtomId)?.Name ?? "";
                if (name.Length != 1)
                    throw new PrologRuntimeException(
                        "type_error", "character", engine, head);
                if (!hasUnbound) sb.Append(name[0]);
            }
            cursor = ListCursor.Resolve(engine, aTail);
        }
        if (cursor.Tag is Tag.Ref or Tag.AttVar)
            hasUnbound = true;
        else if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            // Culprit is the WHOLE list argument (§8.16.7.3), not the
            // improper tail alone.
            throw new PrologRuntimeException("type_error", "list", engine, listCell);
        return sb.ToString();
    }

    private static string ReadCodesToString(Activation engine, Cell codesCell, string builtinName)
    {
        var sb = new StringBuilder();
        Cell listStart = Resolve(engine, codesCell);
        Cell cursor = listStart;
        // A bound non-list argument is type_error(list, L) up front.
        if (cursor.Tag is not (Tag.Lis or Tag.Pstr or Tag.Ref or Tag.AttVar)
            && !(cursor.Tag == Tag.Atom && cursor.AsAtomId == AtomTable.EmptyListId))
            throw new PrologRuntimeException("type_error", "list", engine, listStart);
        while (true)
        {
            // A packed run of CODES is consumed in bulk; a packed run of chars
            // falls through to the element loop, where its one-character atom
            // heads raise the ISO element error like any other non-integer.
            if (cursor.Tag == Tag.Pstr && cursor.AsPstrKind == TextKind.Codes
                && cursor.AsPstrLength > 0)
            {
                sb.Append(engine.ReadPstrChain(cursor, out cursor));
                continue;
            }
            if (!ListCursor.TryUncons(engine, cursor, out Cell rawHead, out Cell cTail)) break;
            Cell head = Resolve(engine, rawHead);
            // ISO §8.16.7 / §8.16.8 type errors: a non-int element is
            // type_error(character_code); an unbound element is
            // instantiation_error (precedence rule applies).
            if (head.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            if (head.Tag != Tag.Int)
                throw new PrologRuntimeException(
                    "type_error", "integer", engine, head);
            // BMP-only, same contract as char_code/2 (see AtomCodes above).
            if (head.AsInt < 0 || head.AsInt > char.MaxValue)
                throw new PrologRuntimeException(
                    "representation_error", "character_code");
            sb.Append((char)head.AsInt);
            cursor = ListCursor.Resolve(engine, cTail);
        }
        // A non-nil tail is a partial / non-proper list — ISO reports this
        // as a type_error(list, _) (the entire arg, not just the tail).
        if (cursor.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "list", engine, listStart);
        return sb.ToString();
    }

    private static string ReadCharAtomsToString(Activation engine, Cell charsCell)
    {
        var sb = new StringBuilder();
        Cell listStart = Resolve(engine, charsCell);
        Cell cursor = listStart;
        // A bound non-list is type_error(list, L) before any element is
        // looked at (§8.16.4.3).
        if (cursor.Tag is not (Tag.Lis or Tag.Pstr)
            && !(cursor.Tag == Tag.Atom && cursor.AsAtomId == AtomTable.EmptyListId)
            && cursor.Tag is not (Tag.Ref or Tag.AttVar))
            throw new PrologRuntimeException("type_error", "list", engine, cursor);
        // A packed list is a list, and whether its elements are chars or codes
        // is in its header — not in its tag (ADR-047). A packed CODE list still
        // raises type_error(character) here, but because its elements are
        // integers, which the loop below decides, rather than by assumption.
        while (ListCursor.TryUncons(engine, cursor, out Cell rawHead, out Cell hTail))
        {
            Cell head = Resolve(engine, rawHead);
            if (head.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            if (head.Tag != Tag.Atom)
                throw new PrologRuntimeException("type_error", "character", engine, head);
            string name = AtomTable.GetById(head.AsAtomId)?.Name ?? "";
            if (name.Length != 1)
                throw new PrologRuntimeException("type_error", "character", engine, head);
            sb.Append(name[0]);
            cursor = ListCursor.Resolve(engine, hTail);
        }
        if (cursor.Tag is Tag.Ref or Tag.AttVar)
        {
            Shumway.Core.Diagnostics.ChoicePointTrace.DumpAtSite(
                engine, "ReadCharAtomsToString instantiation_error");
            throw new PrologRuntimeException("instantiation_error");
        }
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
        {
            Shumway.Core.Diagnostics.ChoicePointTrace.DumpAtSite(
                engine, "ReadCharAtomsToString type_error(list)");
            throw new PrologRuntimeException("type_error", "list", engine, listStart);
        }
        return sb.ToString();
    }

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag is not (Tag.Ref or Tag.AttVar)) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}

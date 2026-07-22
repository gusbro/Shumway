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
        // ISO §8.16.1.3: Atom var → instantiation_error; not atom → type_error(atom, Atom).
        if (atomCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (atomCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
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
            int pstrIdx = engine.MakePstr(name);
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
        if (atomCell.Tag == Tag.Ref || strCell.Tag == Tag.Ref)
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
            int listIdx = BuildCharAtomList(engine, name);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }

        if (atomCell.Tag == Tag.Ref)
        {
            // Read direction: Atom is var, so Chars must be a proper list
            // of single-character atoms. If it isn't, ReadCharAtomsToString
            // raises the appropriate type_error / instantiation_error.
            if (charsCell.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            string name = ReadCharAtomsToString(engine, charsCell);
            int atomId = AtomTable.Intern(name, permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }

        // Atom is bound but to something other than an atom — ISO §8.16.4.
        throw new PrologRuntimeException("type_error", "atom");
    }

    // ---------- char_code/2 ----------

    public static bool CharCode(Activation engine)
    {
        Cell charCell = Resolve(engine, engine.GetRegister(0));
        Cell codeCell = Resolve(engine, engine.GetRegister(1));

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
        if (charCell.Tag == Tag.Ref && codeCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        // Bound to something other than atom / int: report the offending
        // argument's expected type AND value. Char takes precedence when
        // both are bound (a non-atom Char is what ISO checks first).
        if (charCell.Tag != Tag.Ref)
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

        // Numeric → list direction.
        if (numCell.Tag == Tag.Int)
        {
            string s = numCell.AsInt.ToString(CultureInfo.InvariantCulture);
            int listIdx = asCodes
                ? BuildIntCodesList(engine, s)
                : BuildCharAtomList(engine, s);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }
        if (numCell.Tag == Tag.Float)
        {
            double v = Cell.DecodeFloat(numCell, engine.GetHeap(numCell.FloatPairedIndex));
            // Round-trippable ISO float form (decimal point + lowercase e) so
            // number_codes/number_chars re-reads it in either direction.
            string s = Number.FormatPrologFloat(v);
            int listIdx = asCodes
                ? BuildIntCodesList(engine, s)
                : BuildCharAtomList(engine, s);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }
        if (numCell.Tag == Tag.BigInt)
        {
            string s = engine.AsBigInt(numCell).ToString(CultureInfo.InvariantCulture);
            int listIdx = asCodes
                ? BuildIntCodesList(engine, s)
                : BuildCharAtomList(engine, s);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }

        // List → numeric direction.
        if (numCell.Tag == Tag.Ref)
        {
            // ISO §8.16.7 / §8.16.8: the list arg must be sufficiently
            // instantiated. If it's also a var the call is doubly
            // ambiguous → instantiation_error.
            if (strCell.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            string s = asCodes
                ? ReadCodesToString(engine, strCell, builtinName)
                : ReadCharAtomsToString(engine, strCell);
            // Full Prolog number syntax (§6.4.4): radix (0x/0o/0b), char
            // code (0'c), BigInteger-size decimals, and floats; not just
            // what long.TryParse knows.
            if (TryBuildPrologNumber(engine, s, out Cell numResult, out int floatIdx))
                return floatIdx >= 0
                    ? engine.UnifyRegisterWithHeapAt(0, floatIdx)
                    : engine.UnifyRegisterWithCell(0, numResult);
            // ISO: text that does not denote a number is a syntax error —
            // a catchable one, so catch/3 can recover.
            throw new PrologRuntimeException("syntax_error", "illegal_number");
        }

        // First arg bound but not a number: ISO type_error(number, _).
        throw new PrologRuntimeException("type_error", "number");
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
        while (i < n && char.IsWhiteSpace(s[i])) i++;   // leading layout
        bool neg = false;
        if (i < n && (s[i] == '-' || s[i] == '+'))      // '+' is the lenient extension
        {
            neg = s[i] == '-';
            i++;
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
        bool sawExponent = false;
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
            if (j < n && char.IsDigit(s[j]))
            {
                sawExponent = true;
                i = j + 1;
                while (i < n && char.IsDigit(s[i])) i++;
            }
        }
        if (!sawFraction && !sawExponent) return false;
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
            if (atomCell.Tag == Tag.Ref && numCell.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            if (atomCell.Tag != Tag.Ref && atomCell.Tag != Tag.Atom)
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
            if (numCell.Tag == Tag.Ref && strCell.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            if (strCell.Tag != Tag.Ref && strCell.Tag != Tag.Pstr)
                throw new PrologRuntimeException("type_error", "string");
            throw new PrologRuntimeException("type_error", "number");
        }
        return engine.UnifyRegisterWithCell(1, Cell.Ref(engine.MakePstr(text)));
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
        if (atomC.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (atomC.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        string atomName = AtomTable.GetById(atomC.AsAtomId)?.Name ?? "";

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
    {
        if (s.Length == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }
        int start = engine.AllocateHeap(2 * s.Length + 1);
        for (int i = 0; i < s.Length; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, Cell.Int(s[i]));
        }
        engine.SetHeap(start + 2 * s.Length, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    private static int BuildCharAtomList(Activation engine, string s)
    {
        if (s.Length == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }
        int start = engine.AllocateHeap(2 * s.Length + 1);
        for (int i = 0; i < s.Length; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            // Code points in the single-char cache range (Latin-1) bypass
            // Intern entirely — pure array index, no lock, no 1-char string
            // allocation per character. Hot on long tokens.
            int code = s[i];
            int atomId = AtomTable.GetSingleCharAtomId(code);
            if (atomId < 0)
                atomId = AtomTable.Intern(s[i].ToString(), permanent: false).Id;
            engine.SetHeap(headIdx, Cell.Atom(atomId));
        }
        engine.SetHeap(start + 2 * s.Length, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    // ---------- List-reading helpers ----------

    private static string ReadCodesToString(Activation engine, Cell codesCell, string builtinName)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, codesCell);
        while (true)
        {
            // A PSTR (double-quoted literal under the default flag) IS a
            // code list; consume its text and continue at its tail.
            if (cursor.Tag == Tag.Pstr)
            {
                sb.Append(engine.ReadPstrChain(cursor, out cursor));
                continue;
            }
            if (cursor.Tag != Tag.Lis) break;
            Cell head = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex));
            // ISO §8.16.7 / §8.16.8 type errors: a non-int element is
            // type_error(character_code); an unbound element is
            // instantiation_error (precedence rule applies).
            if (head.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            if (head.Tag != Tag.Int)
                throw new PrologRuntimeException("type_error", "character_code");
            sb.Append((char)head.AsInt);
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        // A non-nil tail is a partial / non-proper list — ISO reports this
        // as a type_error(list, _) (the entire arg, not just the tail).
        if (cursor.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "list");
        return sb.ToString();
    }

    private static string ReadCharAtomsToString(Activation engine, Cell charsCell)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, charsCell);
        // A PSTR is a CODE list, not a char list; its elements are
        // integers, so the ISO element-type error applies
        // (type_error(character)), not type_error(list).
        if (cursor.Tag == Tag.Pstr)
            throw new PrologRuntimeException("type_error", "character");
        while (cursor.Tag == Tag.Lis)
        {
            Cell head = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex));
            if (head.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            if (head.Tag != Tag.Atom)
                throw new PrologRuntimeException("type_error", "character");
            string name = AtomTable.GetById(head.AsAtomId)?.Name ?? "";
            if (name.Length != 1)
                throw new PrologRuntimeException("type_error", "character");
            sb.Append(name[0]);
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        if (cursor.Tag == Tag.Ref)
        {
            Shumway.Core.Diagnostics.ChoicePointTrace.DumpAtSite(
                engine, "ReadCharAtomsToString instantiation_error");
            throw new PrologRuntimeException("instantiation_error");
        }
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
        {
            Shumway.Core.Diagnostics.ChoicePointTrace.DumpAtSite(
                engine, "ReadCharAtomsToString type_error(list)");
            throw new PrologRuntimeException("type_error", "list");
        }
        return sb.ToString();
    }

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}

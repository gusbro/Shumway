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
/// <para>Phase-9 chunk 131a: every contract-violation throw is now a
/// catchable <see cref="PrologRuntimeException"/> with an ISO-shaped
/// kind, replacing the uncatchable <see cref="InvalidOperationException"/>
/// that earlier phases used. Argument checks honour the ISO precedence
/// from §7.12.2 — instantiation_error before type_error before
/// representation_error.</para>
/// </summary>
public static class AtomCharBuiltins
{
    // ---------- atom_length/2 ----------

    public static bool AtomLength(Engine engine)
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
    public static bool AtomString(Engine engine)
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

    public static bool AtomChars(Engine engine)
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

    public static bool CharCode(Engine engine)
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
            // Chunk 166: ASCII codes hit the cached permanent atom ids
            // — no lock, no allocation. Higher BMP code points fall
            // back to Intern.
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

    public static bool NumberCodes(Engine engine) => NumberConversion(
        engine, asCodes: true, builtinName: "number_codes/2");

    // ---------- number_chars/2 ----------

    public static bool NumberChars(Engine engine) => NumberConversion(
        engine, asCodes: false, builtinName: "number_chars/2");

    private static bool NumberConversion(Engine engine, bool asCodes, string builtinName)
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
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long iv))
                return engine.UnifyRegisterWithCell(0, Cell.Int(iv));
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
            {
                int idx = engine.MakeFloat(dv);
                return engine.UnifyRegisterWithHeapAt(0, idx);
            }
            // ISO: text that does not denote a number is a syntax error —
            // a catchable one, so catch/3 can recover.
            throw new PrologRuntimeException("syntax_error", "illegal_number");
        }

        // First arg bound but not a number: ISO type_error(number, _).
        throw new PrologRuntimeException("type_error", "number");
    }

    // ---------- atom_number/2 ----------

    /// <summary><c>atom_number(?Atom, ?Number)</c> — converts between an
    /// atom and the number it denotes. Unlike <c>number_codes/2</c> this
    /// <em>fails</em> (rather than raising a syntax error) when the atom is
    /// not numeric, matching the conventional <c>atom_number/2</c>.</summary>
    public static bool AtomNumber(Engine engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        if (atomCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            if (long.TryParse(name, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long iv))
                return engine.UnifyRegisterWithCell(1, Cell.Int(iv));
            if (double.TryParse(name, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double dv))
                return engine.UnifyRegisterWithHeapAt(1, engine.MakeFloat(dv));
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
    public static bool NumberString(Engine engine)
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
    private static string? NumberText(Engine engine, Cell numCell)
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
    /// substring extraction. Phase-1 deterministic modes:
    /// <list type="bullet">
    /// <item><c>(+, +, +, ?, ?)</c>: extract the substring at the given
    ///   offset and length.</item>
    /// <item><c>(+, ?, ?, ?, +)</c>: find the first occurrence of
    ///   <c>SubAtom</c> in <c>Atom</c> and bind the three index
    ///   variables accordingly.</item>
    /// </list>
    /// Non-deterministic enumeration of every match is deferred to the
    /// chunk that wires call/N choice-points.</summary>
    public static bool SubAtom(Engine engine)
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

        // Phase-1 limitation: the unsupported modes still bottom out
        // here, but the ISO-appropriate diagnostic is instantiation_error
        // (the missing args ARE the problem). A future chunk widens this
        // builtin to enumerate every match.
        throw new PrologRuntimeException("instantiation_error");
    }

    // ---------- List-building helpers ----------

    private static int BuildIntCodesList(Engine engine, string s)
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

    private static int BuildCharAtomList(Engine engine, string s)
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
            // Chunk 222: code points in the chunk-166 cache range
            // (Latin-1) bypass Intern entirely — pure array index, no
            // lock, no 1-char string allocation per character. Hot
            // path: atom_chars / string_chars on a long token.
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

    private static string ReadCodesToString(Engine engine, Cell codesCell, string builtinName)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, codesCell);
        while (cursor.Tag == Tag.Lis)
        {
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

    private static string ReadCharAtomsToString(Engine engine, Cell charsCell)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, charsCell);
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

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}

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
/// All five share the same minimal Phase-1 contract: the (+, ?) and (?, +)
/// modes work as expected; (-, -) raises an instantiation error.
/// </summary>
public static class AtomCharBuiltins
{
    // ---------- atom_length/2 ----------

    public static bool AtomLength(Engine engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        if (atomCell.Tag != Tag.Atom)
            throw new InvalidOperationException(
                "atom_length/2: first argument must be a bound atom.");
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

        throw new InvalidOperationException(
            "atom_string/2: at least one of Atom, String must be sufficiently instantiated.");
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
            string name = ReadCharAtomsToString(engine, charsCell);
            int atomId = AtomTable.Intern(name, permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }

        throw new InvalidOperationException(
            $"atom_chars/2: first argument must be atom or var; got tag {atomCell.Tag}.");
    }

    // ---------- char_code/2 ----------

    public static bool CharCode(Engine engine)
    {
        Cell charCell = Resolve(engine, engine.GetRegister(0));
        Cell codeCell = Resolve(engine, engine.GetRegister(1));

        if (charCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(charCell.AsAtomId)?.Name ?? "";
            if (name.Length != 1)
                throw new InvalidOperationException(
                    $"char_code/2: first argument must be a single-character atom, "
                    + $"got '{name}'.");
            return engine.UnifyRegisterWithCell(1, Cell.Int(name[0]));
        }

        if (codeCell.Tag == Tag.Int)
        {
            long code = codeCell.AsInt;
            if (code < 0 || code > char.MaxValue)
                throw new InvalidOperationException(
                    $"char_code/2: integer {code} is out of UTF-16 code-unit range.");
            int atomId = AtomTable.Intern(
                ((char)code).ToString(), permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }

        throw new InvalidOperationException(
            "char_code/2: at least one of (Char, Code) must be sufficiently instantiated.");
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
            string s = v.ToString("R", CultureInfo.InvariantCulture);
            // ISO mandates the printed form include a decimal point. "R" on
            // round-number doubles can drop it ("3" instead of "3.0"); patch
            // up the rare case so number_codes(3.0, X) round-trips through
            // number_codes/2 in either direction.
            if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) s += ".0";
            int listIdx = asCodes
                ? BuildIntCodesList(engine, s)
                : BuildCharAtomList(engine, s);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }

        // List → numeric direction.
        if (numCell.Tag == Tag.Ref)
        {
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

        throw new InvalidOperationException(
            $"{builtinName}: first argument must be a number or an unbound variable.");
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
            throw new InvalidOperationException(
                "atom_number/2: at least one of (Atom, Number) must be bound.");
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
            throw new InvalidOperationException(
                "number_string/2: at least one of (Number, String) must be bound.");
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
            string s = v.ToString("R", CultureInfo.InvariantCulture);
            if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) s += ".0";
            return s;
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
        if (atomC.Tag != Tag.Atom)
            throw new InvalidOperationException(
                "sub_atom/5: first argument must be a bound atom.");
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

        throw new InvalidOperationException(
            "sub_atom/5: Phase 1 requires either (Before, Length) ground or SubAtom ground.");
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
            int atomId = AtomTable.Intern(s[i].ToString(), permanent: false).Id;
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
            if (head.Tag != Tag.Int)
                throw new InvalidOperationException(
                    $"{builtinName}: list element must be an integer code; got tag {head.Tag}.");
            sb.Append((char)head.AsInt);
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new InvalidOperationException(
                $"{builtinName}: second argument must be a proper list of integers.");
        return sb.ToString();
    }

    private static string ReadCharAtomsToString(Engine engine, Cell charsCell)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, charsCell);
        while (cursor.Tag == Tag.Lis)
        {
            Cell head = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex));
            if (head.Tag != Tag.Atom)
                throw new InvalidOperationException(
                    "list element must be a single-character atom; got tag " + head.Tag + ".");
            string name = AtomTable.GetById(head.AsAtomId)?.Name ?? "";
            if (name.Length != 1)
                throw new InvalidOperationException(
                    $"list element must be a single-character atom, got '{name}'.");
            sb.Append(name[0]);
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new InvalidOperationException(
                "second argument must be a proper list of single-character atoms.");
        return sb.ToString();
    }

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}

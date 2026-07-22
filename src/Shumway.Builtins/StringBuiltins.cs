using System.Text;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// String-oriented builtins focused on grammar / parsing workflows
/// (see docs/design/pstr-design.md). Strings ride
/// the PSTR representation; the conversions here pair them up with
/// atoms, code lists, and char lists so the surrounding code can pick
/// whichever shape it likes per use case.
///
/// <para>Most predicates operate in one or two modes — the +,+,+ form
/// for verification and the +,+,- form for splitting / concatenating.
/// Full bidirectional forms (e.g. <c>split_string</c> with a variable
/// in argument 1) are deferred; they're rarely used and require
/// non-trivial reverse search.</para>
/// </summary>
public static class StringBuiltins
{
    /// <summary><c>string_length(String, Length)</c> — the integer
    /// length (in chars) of <paramref name="String"/>. <c>String</c>
    /// can be a PSTR or an atom (handy for callers that haven't yet
    /// settled on one representation).</summary>
    public static bool StringLength(Activation engine)
    {
        string s = ReadStringOrAtom(engine, 0, "string_length/2");
        return engine.UnifyRegisterWithCell(1, Cell.Int(s.Length));
    }

    /// <summary><c>string_concat(A, B, AB)</c>. Supported modes:
    /// <list type="bullet">
    /// <item>(+, +, ?): concatenates <c>A</c> and <c>B</c> and unifies
    ///   with <c>AB</c>.</item>
    /// <item>(?, ?, +): with <c>AB</c> ground and one or both of
    ///   <c>A</c>, <c>B</c> unbound, enumerates every prefix/suffix
    ///   split of <c>AB</c> via a runtime CP — same shape as
    ///   <c>atom_concat/3</c>'s split mode.</item>
    /// </list></summary>
    public static bool StringConcat(Activation engine)
    {
        Cell aRaw = engine.GetRegister(0);
        Cell bRaw = engine.GetRegister(1);
        int aIdx = ResolveIndex(engine, aRaw);
        int bIdx = ResolveIndex(engine, bRaw);
        Cell aCell = aIdx >= 0 ? engine.GetHeap(aIdx) : aRaw;
        Cell bCell = bIdx >= 0 ? engine.GetHeap(bIdx) : bRaw;

        bool aGround = aCell.Tag == Tag.Pstr || aCell.Tag == Tag.Atom;
        bool bGround = bCell.Tag == Tag.Pstr || bCell.Tag == Tag.Atom;

        if (aGround && bGround)
        {
            // Lazy concat: when both sides are already PSTRs,
            // build the result by copying A's content into a fresh buffer
            // and pointing the tail at B — no allocation for B's content.
            // Mixed PSTR/atom inputs fall back to the eager path because
            // atoms need to be materialised into a buffer regardless.
            if (aCell.Tag == Tag.Pstr && bCell.Tag == Tag.Pstr)
            {
                int resultIdx = engine.MakePstrConcat(aIdx, bIdx);
                return engine.UnifyRegisterWithCell(2, Cell.Ref(resultIdx));
            }
            string a = ReadStringOrAtom(engine, 0, "string_concat/3");
            string b = ReadStringOrAtom(engine, 1, "string_concat/3");
            int pstrIdx = engine.MakePstr(a + b);
            return engine.UnifyRegisterWithCell(2, Cell.Ref(pstrIdx));
        }

        Cell abCell = Resolve(engine, engine.GetRegister(2));
        if (abCell.Tag != Tag.Pstr && abCell.Tag != Tag.Atom)
            throw new PrologRuntimeException(
                "instantiation_error",
                "string_concat/3 requires either A+B or AB to be ground");
        string ab = ReadStringOrAtom(engine, 2, "string_concat/3");
        int returnPc = engine.BuiltinReturnPc;
        return new StringConcatSplitCursor(ab, returnPc).Start(engine);
    }

    /// <summary>Resume state for the non-deterministic <c>string_concat/3</c>
    /// split: the string being split and the running split index, plus a
    /// cached resume delegate — allocated once per call, re-pushed unchanged
    /// on each backtrack (no per-split closure).</summary>
    private sealed class StringConcatSplitCursor
    {
        private readonly string _ab;
        private readonly int _returnPc;
        private int _splitIdx;
        public readonly Func<Activation, int, bool> Resume;

        public StringConcatSplitCursor(string ab, int returnPc)
        {
            _ab = ab;
            _returnPc = returnPc;
            _splitIdx = 0;
            Resume = (e, _) => Attempt(e, isResume: true);
        }

        public bool Start(Activation engine) => Attempt(engine, isResume: false);

        private bool Attempt(Activation engine, bool isResume)
        {
            int splitIdx = _splitIdx;
            if (splitIdx > _ab.Length) return false;
            if (splitIdx < _ab.Length)
            {
                _splitIdx = splitIdx + 1;
                engine.PushBuiltinChoicePoint(Resume, arity: 3);  // restore string_concat/3 args
            }
            int aPstr = engine.MakePstr(_ab.Substring(0, splitIdx));
            int bPstr = engine.MakePstr(_ab.Substring(splitIdx));
            if (!engine.UnifyRegisterWithCell(0, Cell.Ref(aPstr))) return false;
            if (!engine.UnifyRegisterWithCell(1, Cell.Ref(bPstr))) return false;
            if (isResume) engine.ResumeAtReturnPc(_returnPc);
            return true;
        }
    }

    /// <summary><c>string_chars(String, Chars)</c> — bidirectional
    /// <c>String ↔ list-of-single-character-atoms</c>. With both args
    /// bound it verifies the relation; with one var it builds the
    /// other.</summary>
    public static bool StringChars(Activation engine)
    {
        Cell strCell = Resolve(engine, engine.GetRegister(0));
        if (strCell.Tag == Tag.Pstr || strCell.Tag == Tag.Atom)
        {
            string s = ReadStringOrAtom(engine, 0, "string_chars/2");
            int listIdx = BuildCharAtomList(engine, s);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }

        if (strCell.Tag == Tag.Ref)
        {
            string s = ReadCharAtomsToString(engine, engine.GetRegister(1), "string_chars/2");
            int pstrIdx = engine.MakePstr(s);
            return engine.UnifyRegisterWithCell(0, Cell.Ref(pstrIdx));
        }

        throw new PrologRuntimeException("type_error", "string");
    }

    /// <summary><c>string_codes(String, Codes)</c> — bidirectional
    /// <c>String ↔ list-of-character-codes</c>.</summary>
    public static bool StringCodes(Activation engine)
    {
        Cell strCell = Resolve(engine, engine.GetRegister(0));
        if (strCell.Tag == Tag.Pstr || strCell.Tag == Tag.Atom)
        {
            string s = ReadStringOrAtom(engine, 0, "string_codes/2");
            int listIdx = BuildCodeList(engine, s);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }

        if (strCell.Tag == Tag.Ref)
        {
            string s = ReadCodesToString(engine, engine.GetRegister(1), "string_codes/2");
            int pstrIdx = engine.MakePstr(s);
            return engine.UnifyRegisterWithCell(0, Cell.Ref(pstrIdx));
        }

        throw new PrologRuntimeException("type_error", "string");
    }

    /// <summary><c>split_string(String, SepChars, PadChars, Parts)</c>
    /// — splits <paramref name="String"/> at every char in
    /// <paramref name="SepChars"/>, then trims leading / trailing chars
    /// in <paramref name="PadChars"/> from each piece. Mirrors SWI's
    /// behaviour: empty <c>SepChars</c> means "no splitting" (whole
    /// string returned, trimmed). Pieces are PSTRs in the resulting
    /// list. Only the +,+,+,? mode is supported.</summary>
    public static bool SplitString(Activation engine)
    {
        string s = ReadStringOrAtom(engine, 0, "split_string/4");
        string seps = ReadStringOrAtom(engine, 1, "split_string/4");
        string pads = ReadStringOrAtom(engine, 2, "split_string/4");

        var pieces = new List<string>();
        if (seps.Length == 0)
        {
            pieces.Add(s);
        }
        else
        {
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (seps.IndexOf(s[i]) >= 0)
                {
                    pieces.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            pieces.Add(s.Substring(start));
        }

        if (pads.Length > 0)
        {
            char[] padArr = pads.ToCharArray();
            for (int i = 0; i < pieces.Count; i++)
                pieces[i] = pieces[i].Trim(padArr);
        }

        int listIdx = BuildPstrList(engine, pieces);
        return engine.UnifyRegisterWithHeapAt(3, listIdx);
    }

    /// <summary><c>upcase_atom(Atom, Upper)</c> — uppercase the atom's
    /// name. Result is an atom (not a string) to match SWI.</summary>
    public static bool UpcaseAtom(Activation engine)
    {
        Cell src = Resolve(engine, engine.GetRegister(0));
        if (src.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        string name = AtomTable.GetById(src.AsAtomId)?.Name ?? "";
        int atomId = AtomTable.Intern(
            name.ToUpper(System.Globalization.CultureInfo.InvariantCulture),
            permanent: false).Id;
        return engine.UnifyRegisterWithCell(1, Cell.Atom(atomId));
    }

    /// <summary><c>downcase_atom(Atom, Lower)</c> — lowercase the
    /// atom's name.</summary>
    public static bool DowncaseAtom(Activation engine)
    {
        Cell src = Resolve(engine, engine.GetRegister(0));
        if (src.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        string name = AtomTable.GetById(src.AsAtomId)?.Name ?? "";
        int atomId = AtomTable.Intern(
            name.ToLower(System.Globalization.CultureInfo.InvariantCulture),
            permanent: false).Id;
        return engine.UnifyRegisterWithCell(1, Cell.Atom(atomId));
    }

    // ---------- Helpers ----------

    private static string ReadStringOrAtom(Activation engine, int regIdx, string builtinName)
    {
        Cell c = Resolve(engine, engine.GetRegister(regIdx));
        if (c.Tag == Tag.Atom)
            return AtomTable.GetById(c.AsAtomId)?.Name ?? "";
        if (c.Tag == Tag.Pstr)
            return engine.AsPstrString(engine.Deref(engine.GetRegister(regIdx).AsHeapIndex));
        if (c.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        throw new PrologRuntimeException("type_error", $"{builtinName}: string or atom");
    }

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }

    /// <summary>Returns the heap index where the dereferenced cell
    /// lives, or <c>-1</c> when <paramref name="c"/> isn't a Ref (no
    /// heap address to talk about — the value already lives in the
    /// register). Used by the lazy <c>string_concat</c> when it needs
    /// the source PSTR header's address to chain a new header to it.</summary>
    private static int ResolveIndex(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return -1;
        return engine.Deref(c.AsHeapIndex);
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
            // Single-char atom cache: see AtomCharBuiltins.BuildCharAtomList.
            int code = s[i];
            int atomId = AtomTable.GetSingleCharAtomId(code);
            if (atomId < 0)
                atomId = AtomTable.Intern(s[i].ToString(), permanent: false).Id;
            engine.SetHeap(headIdx, Cell.Atom(atomId));
        }
        engine.SetHeap(start + 2 * s.Length, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    private static int BuildCodeList(Activation engine, string s)
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

    private static int BuildPstrList(Activation engine, List<string> pieces)
    {
        if (pieces.Count == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }
        // Allocate the spine first, then the PSTR payloads inside each
        // cell. We allocate one Lis + one head cell per piece; the
        // PSTR's own heap layout lives wherever MakePstr lands it.
        int spine = engine.AllocateHeap(2 * pieces.Count + 1);
        for (int i = 0; i < pieces.Count; i++)
        {
            int lisIdx = spine + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            int pstrIdx = engine.MakePstr(pieces[i]);
            engine.SetHeap(headIdx, Cell.Ref(pstrIdx));
        }
        engine.SetHeap(spine + 2 * pieces.Count, Cell.Atom(AtomTable.EmptyListId));
        return spine;
    }

    private static string ReadCharAtomsToString(Activation engine, Cell charsCell, string builtinName)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, charsCell);
        while (cursor.Tag == Tag.Lis)
        {
            Cell head = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex));
            if (head.Tag != Tag.Atom)
                throw new PrologRuntimeException("type_error",
                    $"{builtinName}: list element must be a single-character atom");
            string name = AtomTable.GetById(head.AsAtomId)?.Name ?? "";
            if (name.Length != 1)
                throw new PrologRuntimeException("type_error",
                    $"{builtinName}: list element must be exactly one character");
            sb.Append(name[0]);
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error",
                $"{builtinName}: argument must be a proper list");
        return sb.ToString();
    }

    private static string ReadCodesToString(Activation engine, Cell codesCell, string builtinName)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, codesCell);
        while (cursor.Tag == Tag.Lis)
        {
            Cell head = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex));
            if (head.Tag != Tag.Int)
                throw new PrologRuntimeException("type_error",
                    $"{builtinName}: list element must be a character code");
            sb.Append((char)head.AsInt);
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error",
                $"{builtinName}: argument must be a proper list");
        return sb.ToString();
    }
}

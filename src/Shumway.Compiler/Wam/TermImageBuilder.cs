using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>ADR-063: lays a compound out as the heap would hold it, every
/// index relative to the first cell (<see cref="TermImage"/>). A variable is
/// a REF to its own cell; every cell of a named one is a hole, the cell the
/// stream meets first as the <see cref="HoleClassifier"/> decides (the
/// register it comes from or goes to), the others from that register; an
/// anonymous one is a fresh cell and nothing more. Null for a term an image
/// cannot carry: a big integer or a rational (their cells name a table of
/// the activation), a text past the packed string's length, a named
/// variable with no classifier. No recursion: the compounds still to lay
/// wait on a stack, so a long list's spine or a long left-nested chain is a
/// loop.</summary>
internal static class TermImageBuilder
{
    /// <summary>The hole for the cell of a named variable the stream meets
    /// first, at <c>offset</c>: which register, in or out. Called once per
    /// variable, in the image's order, and may take registers.</summary>
    public delegate ImageHole HoleClassifier(string name, int offset);

    /// <summary>The holes the image of <paramref name="term"/> would need:
    /// the named variables in it, each once. No side effect, for the
    /// threshold test before building.</summary>
    public static int HoleCount(Term term)
    {
        var names = new HashSet<string>();
        var pending = new Stack<Term>();
        pending.Push(term);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case CompoundTerm c:
                    foreach (var a in c.Args) pending.Push(a);
                    break;
                case VarTerm v when v.Name != "_":
                    names.Add(v.Name);
                    break;
            }
        }
        return names.Count;
    }
    /// <summary>The cells the term takes on the heap, counted without laying
    /// it out: the threshold test before building an image.</summary>
    public static int CellCount(Term term)
    {
        int n = 0;
        var pending = new Stack<Term>();
        pending.Push(term);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case CompoundTerm c:
                    n += IsCons(c) ? 2 : 1 + c.Args.Length;
                    foreach (var a in c.Args) pending.Push(a);
                    break;
                case FloatTerm:
                    n += 2;
                    break;
                case StringTerm s:
                    n += 2 + (s.Content.Length + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
                    break;
            }
        }
        return n;
    }

    public static TermImage? Build(Term term) => Build(term, null);

    /// <summary><paramref name="blocks"/>, when given, takes the offset of
    /// each compound's block, by the compound.</summary>
    public static TermImage? Build(Term term, HoleClassifier? classify, IDictionary<CompoundTerm, int>? blocks = null)
    {
        if (term is not CompoundTerm root) return null;
        var cells = new List<Cell>();
        var holes = new List<ImageHole>();
        if (!Lay(root, cells, holes, classify, blocks)) return null;
        return new TermImage(cells.ToArray(), IsCons(root) ? Tag.Lis : Tag.Str,
            holes.Count == 0 ? null : holes.ToArray());
    }

    private static bool IsCons(CompoundTerm c) => c.Functor == "." && c.Args.Length == 2;

    // A compound's block goes at the end of the list when its turn comes,
    // and the argument cell that refers to it is written then, with the
    // index the block starts at. Scalars go in place; a float's pair and a
    // packed string's cells go at the end as they are met.
    private static bool Lay(CompoundTerm root, List<Cell> cells, List<ImageHole> holes, HoleClassifier? classify,
        IDictionary<CompoundTerm, int>? blocks)
    {
        // A named variable's cells as laid, and the one the stream meets first.
        Dictionary<string, List<int>>? named = null;
        List<string>? names = null;
        Dictionary<string, int>? firstCell = null;
        Dictionary<string, (CompoundTerm Owner, int Index)>? streamFirst = null;
        var pending = new Stack<(int Slot, CompoundTerm Term)>();
        pending.Push((-1, root));
        while (pending.Count > 0)
        {
            var (slot, c) = pending.Pop();
            int start = cells.Count;
            if (blocks is not null) blocks[c] = start;
            if (slot >= 0) cells[slot] = IsCons(c) ? Cell.Lis(start) : Cell.Str(start);
            int argBase;
            if (IsCons(c))
            {
                cells.Add(default);
                cells.Add(default);
                argBase = start;
            }
            else
            {
                cells.Add(Cell.Functor(FunctorTable.Intern(InternAtom(c.Functor), c.Args.Length)));
                for (int i = 0; i < c.Args.Length; i++) cells.Add(default);
                argBase = start + 1;
            }
            for (int i = 0; i < c.Args.Length; i++)
            {
                switch (c.Args[i])
                {
                    case AtomTerm a:
                        cells[argBase + i] = Cell.Atom(a.Name == "[]" ? AtomTable.EmptyListId : InternAtom(a.Name));
                        break;
                    case IntTerm n:
                        if (n.Value < Cell.MinInt60 || n.Value > Cell.MaxInt60) return false;
                        cells[argBase + i] = Cell.Int(n.Value);
                        break;
                    case FloatTerm f:
                    {
                        // The argument refers to the header, as a put_float's
                        // register does; the pair follows the header.
                        int h = cells.Count;
                        var (header, paired) = Cell.MakeFloat(f.Value, h + 1);
                        cells.Add(header);
                        cells.Add(paired);
                        cells[argBase + i] = Cell.Ref(h);
                        break;
                    }
                    case StringTerm s:
                    {
                        int h = LayPstr(s, cells);
                        if (h < 0) return false;
                        cells[argBase + i] = Cell.Ref(h);
                        break;
                    }
                    case CompoundTerm sub:
                        pending.Push((argBase + i, sub));
                        break;
                    case VarTerm v when v.Name == "_":
                        cells[argBase + i] = Cell.Ref(argBase + i);   // unbound once relocated
                        break;
                    case VarTerm v:
                    {
                        if (classify is null) return false;
                        int at = argBase + i;
                        cells[at] = Cell.Ref(at);
                        named ??= new Dictionary<string, List<int>>();
                        names ??= new List<string>();
                        firstCell ??= new Dictionary<string, int>();
                        streamFirst ??= StreamFirst(root);
                        if (!named.TryGetValue(v.Name, out var its))
                        {
                            named[v.Name] = its = new List<int>();
                            names.Add(v.Name);
                        }
                        its.Add(at);
                        var sf = streamFirst[v.Name];
                        if (ReferenceEquals(sf.Owner, c) && sf.Index == i) firstCell[v.Name] = at;
                        break;
                    }
                    default:
                        return false;   // a big integer, a rational
                }
            }
        }
        if (named is not null)
        {
            // The cell the stream meets first as the classifier says, the
            // others from the same register, after every first: the copy
            // applies the holes in order.
            var others = new List<ImageHole>();
            foreach (string name in names!)
            {
                int first = firstCell![name];
                var hole = classify!(name, first);
                holes.Add(hole);
                byte kind = (byte)(hole.Kind & ImageHole.Y);   // In, of the frame or not
                foreach (int at in named[name])
                    if (at != first) others.Add(new ImageHole(at, kind, hole.Slot));
            }
            holes.AddRange(others);
        }
        return true;
    }

    // The argument the stream meets first for each named variable: the order
    // the streams run (ADR-063), a compound's arguments in order, the last
    // one inline when it is a compound the stream builds inline, the other
    // compounds after it first in first out. The matcher takes the blocks in
    // this order (Activation.MatchImage).
    private static Dictionary<string, (CompoundTerm Owner, int Index)> StreamFirst(CompoundTerm root)
    {
        var first = new Dictionary<string, (CompoundTerm Owner, int Index)>();
        var queue = new Queue<CompoundTerm>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            while (true)
            {
                int n = c.Args.Length;
                CompoundTerm? inline = null;
                for (int i = 0; i < n; i++)
                {
                    switch (c.Args[i])
                    {
                        case VarTerm v when v.Name != "_":
                            if (!first.ContainsKey(v.Name)) first[v.Name] = (c, i);
                            break;
                        case CompoundTerm sub:
                            if (i == n - 1 && ClauseCompiler.CanInlineCompound(sub)) inline = sub;
                            else queue.Enqueue(sub);
                            break;
                    }
                }
                if (inline is null) break;
                c = inline;
            }
        }
        return first;
    }

    // The packed string as Activation.MakePstr lays it: the header, the
    // buffer cells of three code units, the tail cell holding [].
    private static int LayPstr(StringTerm s, List<Cell> cells)
    {
        string value = s.Content;
        int codeUnits = value.Length;
        if (codeUnits > Cell.MaxPstrLength) return -1;
        int bufferCellCount = (codeUnits + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
        int header = cells.Count;
        int bufferStart = header + 1;
        cells.Add(default);
        bool astral = false;
        for (int i = 0; i < bufferCellCount; i++)
        {
            int basePos = i * Cell.PstrCodeUnitsPerBuffer;
            int cu0 = basePos < codeUnits ? value[basePos] : 0;
            int cu1 = basePos + 1 < codeUnits ? value[basePos + 1] : 0;
            int cu2 = basePos + 2 < codeUnits ? value[basePos + 2] : 0;
            astral |= (cu0 & 0xF800) == 0xD800 || (cu1 & 0xF800) == 0xD800 || (cu2 & 0xF800) == 0xD800;
            cells.Add(Cell.PstrBuffer(cu0, cu1, cu2));
        }
        cells.Add(Cell.Atom(AtomTable.EmptyListId));
        cells[header] = Cell.Pstr(codeUnits, bufferStart, 0, s.Kind, astral);
        return header;
    }

    private static int InternAtom(string name) => AtomTable.Intern(name, permanent: true).Id;
}

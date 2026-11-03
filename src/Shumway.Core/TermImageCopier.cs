namespace Shumway.Core;

/// <summary>ADR-063: the image of a structure as it stands on the heap, for a
/// fact asserted at run time. The cells are laid as the heap holds them, every
/// index relative to the first; a sub-term two arguments share is laid once;
/// a variable is a REF to its own cell, an attributed one a plain variable (a
/// copy carries no attributes). Null for a term an image cannot carry: a big
/// integer, a rational or a foreign object, whose cells name a table of the
/// activation, and a cyclic term. Iterative: the term is user data of any
/// depth.</summary>
public static class TermImageCopier
{
    /// <summary>The image of the structure <paramref name="value"/> names, or
    /// null: an atom, a list or a variable makes no image here.</summary>
    public static TermImage? FromHeap(Activation engine, Cell value)
    {
        Cell c = value;
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag != Tag.Str) return null;

        var vars = engine.ImageCopyVars ??= new AddressMap();
        var blocks = engine.ImageCopyBlocks ??= new AddressMap();
        var active = engine.ImageCopyActive ??= new HashSet<int>();
        var work = engine.ImageCopyWork ??= new List<(Cell Value, int Addr, int Dest)>();
        vars.Clear();
        blocks.Clear();
        active.Clear();
        work.Clear();
        Cell[] cells = engine.ImageCopyCells ??= new Cell[64];
        int length = 0;

        int Reserve(int n)
        {
            int start = length;
            length += n;
            if (length > cells.Length)
            {
                Array.Resize(ref cells, Math.Max(length, cells.Length * 2));
                engine.ImageCopyCells = cells;
            }
            return start;
        }

        // An argument into a cell of the image: an atom or an integer now, the
        // rest when its turn comes.
        void Next(int addr, int dest)
        {
            Cell a = engine.GetHeap(addr);
            Cell v = a.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(a.AsHeapIndex)) : a;
            if (v.Tag is Tag.Atom or Tag.Int) cells[dest] = v;
            else work.Add((a, addr, dest));
        }

        // A structure's or a list's block at heap index `at`: its cells, and its
        // arguments queued behind the mark that takes the block off the path
        // from the root, so that a reference back to a block on the path is a
        // cycle and one to a block off it a shared sub-term.
        int Lay(int at, bool list)
        {
            int k;
            if (list)
            {
                k = Reserve(2);
                blocks.Add(at, k);
                active.Add(at);
                work.Add((default, at, ~at));
                Next(at + 1, k + 1);
                Next(at, k);
            }
            else
            {
                Cell functor = engine.GetHeap(at);
                int arity = FunctorTable.Lookup(functor.AsFunctorId).Arity;
                k = Reserve(1 + arity);
                blocks.Add(at, k);
                active.Add(at);
                work.Add((default, at, ~at));
                cells[k] = functor;
                for (int i = arity - 1; i >= 0; i--) Next(at + 1 + i, k + 1 + i);
            }
            return k;
        }

        Lay(c.AsHeapIndex, list: false);
        while (work.Count > 0)
        {
            var (v, a, dest) = work[^1];
            work.RemoveAt(work.Count - 1);
            if (dest < 0)
            {
                active.Remove(~dest);
                continue;
            }
            if (v.Tag == Tag.Ref)
            {
                a = engine.Deref(v.AsHeapIndex);
                v = engine.GetHeap(a);
            }
            switch (v.Tag)
            {
                case Tag.Ref:
                case Tag.AttVar:
                    if (vars.TryGetValue(a, out int home))
                    {
                        cells[dest] = Cell.Ref(home);
                    }
                    else
                    {
                        vars.Add(a, dest);
                        cells[dest] = Cell.Ref(dest);
                    }
                    break;
                case Tag.Atom:
                case Tag.Int:
                    cells[dest] = v;
                    break;
                case Tag.Str:
                case Tag.Lis:
                {
                    int at = v.AsHeapIndex;
                    if (blocks.TryGetValue(at, out int k))
                    {
                        if (active.Contains(at)) return null;
                    }
                    else
                    {
                        k = Lay(at, v.Tag == Tag.Lis);
                    }
                    cells[dest] = v.Tag == Tag.Lis ? Cell.Lis(k) : Cell.Str(k);
                    break;
                }
                case Tag.Float:
                {
                    // Keyed by the complement of the header's address, as a block
                    // can start where a header does.
                    if (!blocks.TryGetValue(~a, out int k))
                    {
                        k = Reserve(2);
                        blocks.Add(~a, k);
                        var (header, bits) = Cell.MakeFloat(
                            Cell.DecodeFloat(v, engine.GetHeap(v.FloatPairedIndex)), k + 1);
                        cells[k] = header;
                        cells[k + 1] = bits;
                    }
                    cells[dest] = Cell.Ref(k);
                    break;
                }
                case Tag.Pstr:
                {
                    if (!blocks.TryGetValue(~a, out int k))
                    {
                        int n = v.AsPstrLength;
                        int units = (n + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
                        k = Reserve(1 + units + 1);
                        blocks.Add(~a, k);
                        cells[k] = Cell.Pstr(n, k + 1, 0, v.AsPstrKind, v.AsPstrIsAstral);
                        CopyCodeUnits(engine, v, cells, k + 1, units);
                        int tail = engine.GetPstrTailIndexOf(v);
                        work.Add((engine.GetHeap(tail), tail, k + 1 + units));
                    }
                    cells[dest] = Cell.Ref(k);
                    break;
                }
                default:
                    return null;
            }
        }
        var image = new Cell[length];
        Array.Copy(cells, image, length);
        return new TermImage(image, Tag.Str);
    }

    // The code units of the packed string `header` names, packed from the start
    // of the cells at `at`: a header past the start of its buffer (the tail of
    // another string) begins mid-cell.
    private static void CopyCodeUnits(Activation engine, Cell header, Cell[] cells, int at, int units)
    {
        int buffer = header.AsPstrBufferIndex;
        int offset = header.AsPstrOffset;
        if (offset == 0)
        {
            for (int j = 0; j < units; j++) cells[at + j] = engine.GetHeap(buffer + j);
            return;
        }
        int length = header.AsPstrLength;
        for (int j = 0; j < units; j++)
        {
            int u = j * Cell.PstrCodeUnitsPerBuffer;
            cells[at + j] = Cell.PstrBuffer(Unit(u), Unit(u + 1), Unit(u + 2));
        }

        int Unit(int i) => i < length
            ? engine.GetHeap(buffer + (offset + i) / Cell.PstrCodeUnitsPerBuffer)
                .AsPstrCodeUnit((offset + i) % Cell.PstrCodeUnitsPerBuffer)
            : 0;
    }
}

using System.Collections.Generic;
using System.Numerics;

namespace Shumway.Core;

/// <summary>
/// The solutions a findall/3, findall/4, bagof/3 or setof/3 call has recorded,
/// held off the heap: the <c>fail</c> that drives the enumeration takes back
/// everything the goal built, so each solution is copied out as it is found and
/// copied back when the call collects.
///
/// <para>One array holds every solution, one after the other, each laid out as
/// the heap would lay it with every heap index relative to the array's start.
/// Collecting copies the array to the heap in one block and moves it by one delta
/// (<see cref="TermImage.Relocate"/>), so a solution costs no object of its own.
/// The array is a first part that grows up to a chunk and whole chunks after it: a
/// large buffer neither copies itself to grow nor asks for a block big enough for
/// the large object heap. A float and a packed string are blocks of the array, as
/// in a term image (ADR-063). A big integer, a rational or a foreign object is a
/// side-table entry of the activation that made it: its value waits aside and is
/// made again when the solution is copied back. Variables are fresh in each
/// solution: sharing inside one solution survives, sharing with anything else
/// does not, and attributes are not copied.</para>
///
/// <para>A findall solution starts with its pair of the result list: the root
/// value is the head, and the tail links to the next solution's pair, so the copy
/// back is the list. A solution added on its own (a bagof/setof pair, the value of
/// a global variable) starts with its root value and is copied back on its
/// own.</para>
/// </summary>
public sealed class SolutionBuffer
{
    private const int ChunkBits = 12;
    private const int ChunkSize = 1 << ChunkBits;
    // The chunks Reset keeps for the next call: the activation keeps the
    // buffer for its life, so a large one is not kept whole.
    private const int RetainedChunks = 16;
    private Cell[] _first = new Cell[16];
    private List<Cell[]>? _chunks;
    private int _length;
    private int _count;
    // The last list pair, whose tail the next findall solution links, and where
    // that tail is.
    private int _lastPair = -1;
    private Cell[]? _lastTail;
    private int _lastTailAt;
    // Where each solution added on its own starts.
    private int[]? _starts;
    // The cells that stand for a side-table value, and the values, in step; per
    // solution added on its own, where its sites start, once one has any.
    private List<int>? _payloadSites;
    private List<object?>? _payloads;
    private int[]? _payloadFrom;

    /// <summary>bagof/setof: each solution's witness key.</summary>
    public List<string>? Keys;

    public int Count => _count;

    /// <summary>Empties the buffer for another call, keeping its first part and
    /// up to <see cref="RetainedChunks"/> chunks.</summary>
    public void Reset()
    {
        _length = 0;
        _count = 0;
        _lastPair = -1;
        _lastTail = null;
        _lastTailAt = 0;
        _payloadSites = null;
        _payloads = null;
        _payloadFrom = null;
        Keys = null;
        if (_chunks is { Count: > RetainedChunks } chunks)
            chunks.RemoveRange(RetainedChunks, chunks.Count - RetainedChunks);
    }

    /// <summary>findall: copies the term <paramref name="value"/> names (a
    /// register's cell) in as the next pair of the list <see cref="EmitList"/>
    /// copies back.</summary>
    public void AddToList(Activation engine, Cell value)
    {
        Cell c = value;
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        int pair = _length;
        if (c.Tag is Tag.Atom or Tag.Int)
        {
            // Written in place when the pair fits in the part the next cell is in.
            Cell[]? part = null;
            int i = 0;
            if (pair + 2 <= _first.Length)
            {
                part = _first;
                i = pair;
            }
            else if (pair >= ChunkSize)
            {
                int j = pair - ChunkSize;
                i = j & (ChunkSize - 1);
                if (i + 2 <= ChunkSize && _chunks is { } chunks && (j >> ChunkBits) < chunks.Count)
                    part = chunks[j >> ChunkBits];
            }
            if (part is not null)
            {
                part[i] = c;
                if (_lastTail is not null) _lastTail[_lastTailAt] = Cell.Lis(pair);
                _lastTail = part;
                _lastTailAt = i + 1;
                _lastPair = pair;
                _length = pair + 2;
                _count++;
                return;
            }
        }
        pair = Reserve(2);
        if (_lastTail is not null) _lastTail[_lastTailAt] = Cell.Lis(pair);
        _lastTailAt = Locate(pair + 1, out _lastTail);
        _lastPair = pair;
        _count++;
        Copy(engine, value, pair);
    }

    /// <summary>bagof/setof, a global variable: copies the term
    /// <paramref name="value"/> names in as a solution <see cref="Emit"/> copies
    /// back on its own.</summary>
    public void Add(Activation engine, Cell value)
    {
        _starts ??= new int[4];
        if (_count == _starts.Length)
        {
            System.Array.Resize(ref _starts, _count * 2);
            if (_payloadFrom is not null) System.Array.Resize(ref _payloadFrom, _count * 2);
        }
        int root = Reserve(1);
        _starts[_count] = root;
        if (_payloadFrom is not null) _payloadFrom[_count] = _payloadSites!.Count;
        _count++;
        Copy(engine, value, root);
    }

    /// <summary>A buffer with this one's solutions that holds exactly their
    /// cells: what a value kept for long (a record) holds, while this one goes
    /// back to be reused. A buffer past its first part is returned as it is.</summary>
    public SolutionBuffer Compact()
    {
        if (_chunks is not null) return this;
        var copy = new SolutionBuffer
        {
            _first = new Cell[System.Math.Max(_length, 1)],
            _length = _length,
            _count = _count,
            _lastPair = _lastPair,
        };
        System.Array.Copy(_first, copy._first, _length);
        if (_starts is not null)
        {
            copy._starts = new int[System.Math.Max(_count, 1)];
            System.Array.Copy(_starts, copy._starts, _count);
        }
        if (_payloadSites is not null)
        {
            copy._payloadSites = new List<int>(_payloadSites);
            copy._payloads = new List<object?>(_payloads!);
            if (_payloadFrom is not null)
            {
                copy._payloadFrom = new int[System.Math.Max(_count, 1)];
                System.Array.Copy(_payloadFrom, copy._payloadFrom, _count);
            }
        }
        return copy;
    }

    /// <summary>True when solution <paramref name="i"/>, added on its own,
    /// certainly does not unify with the term at heap cell <paramref
    /// name="heapIdx"/>: they differ in an atom, an integer, a functor or a
    /// list cell within <paramref name="depth"/> levels. False when they may;
    /// a variable, a float, a packed string or a side-table value on either
    /// side is a may.</summary>
    public bool DefiniteMismatch(Activation engine, int i, int heapIdx, int depth)
        => Mismatch(engine, heapIdx, At(_starts![i]), depth);

    private bool Mismatch(Activation engine, int heapIdx, Cell b, int depth)
    {
        if (depth <= 0) return false;
        int idx = engine.Deref(heapIdx);
        Cell h = engine.GetHeap(idx);
        switch (h.Tag)
        {
            case Tag.Atom:
                return b.Tag switch
                {
                    Tag.Atom => b.AsAtomId != h.AsAtomId,
                    Tag.Int or Tag.Str or Tag.Lis => true,
                    _ => false,
                };
            case Tag.Int:
                return b.Tag switch
                {
                    Tag.Int => b.AsInt != h.AsInt,
                    Tag.Atom or Tag.Str or Tag.Lis => true,
                    _ => false,
                };
            case Tag.Str:
            {
                if (b.Tag is Tag.Atom or Tag.Int) return true;
                if (b.Tag != Tag.Str) return false;
                Cell f = engine.GetHeap(h.AsHeapIndex);
                Cell bf = At(b.AsHeapIndex);
                if (f.Tag != Tag.Functor || bf.Tag != Tag.Functor) return false;
                if (f.AsFunctorId != bf.AsFunctorId) return true;
                int arity = FunctorTable.Lookup(f.AsFunctorId).Arity;
                for (int k = 1; k <= arity; k++)
                    if (Mismatch(engine, h.AsHeapIndex + k, At(b.AsHeapIndex + k), depth - 1))
                        return true;
                return false;
            }
            case Tag.Lis:
                if (b.Tag is Tag.Atom or Tag.Int) return true;
                if (b.Tag != Tag.Lis) return false;
                return Mismatch(engine, h.AsHeapIndex, At(b.AsHeapIndex), depth - 1)
                    || Mismatch(engine, h.AsHeapIndex + 1, At(b.AsHeapIndex + 1), depth - 1);
            default:
                return false;
        }
    }

    /// <summary>Copies the term <paramref name="value"/> names into cell
    /// <paramref name="root"/> and the cells it reserves after it. Iterative: a
    /// solution is user data of any depth.</summary>
    private void Copy(Activation engine, Cell value, int root)
    {
        Cell c = value;
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag is Tag.Atom or Tag.Int)
        {
            At(root) = c;
            return;
        }

        var vars = engine.SolutionCopyVars ??= new AddressMap();
        // Structures and lists by their first cell's address; a float's or a
        // packed string's header by the complement of its address, since a list
        // pair's head cell can be one.
        var blocks = engine.SolutionCopyBlocks ??= new AddressMap();
        var work = engine.SolutionCopyWork ??= new List<(Cell, int, int)>();
        vars.Clear();
        blocks.Clear();
        work.Clear();
        work.Add((value, -1, root));
        while (work.Count > 0)
        {
            int a;
            (c, a, int dest) = work[^1];
            work.RemoveAt(work.Count - 1);
            if (c.Tag == Tag.Ref)
            {
                a = engine.Deref(c.AsHeapIndex);
                c = engine.GetHeap(a);
            }
            switch (c.Tag)
            {
                case Tag.Ref:
                    Variable(vars, a, dest);
                    break;
                case Tag.AttVar:
                    Variable(vars, a >= 0 ? a : engine.Deref(c.AsHeapIndex), dest);
                    break;
                case Tag.Atom:
                case Tag.Int:
                    At(dest) = c;
                    break;
                case Tag.Str:
                {
                    int f = c.AsHeapIndex;
                    if (!blocks.TryGetValue(f, out int k))
                    {
                        Cell functor = engine.GetHeap(f);
                        int arity = FunctorTable.Lookup(functor.AsFunctorId).Arity;
                        k = Reserve(1 + arity);
                        blocks.Add(f, k);
                        At(k) = functor;
                        for (int i = arity - 1; i >= 0; i--)
                            Next(engine, work, f + 1 + i, k + 1 + i);
                    }
                    At(dest) = Cell.Str(k);
                    break;
                }
                case Tag.Lis:
                {
                    int h = c.AsHeapIndex;
                    if (!blocks.TryGetValue(h, out int k))
                    {
                        k = Reserve(2);
                        blocks.Add(h, k);
                        Next(engine, work, h + 1, k + 1);
                        Next(engine, work, h, k);
                    }
                    At(dest) = Cell.Lis(k);
                    break;
                }
                case Tag.Float:
                {
                    if (a < 0 || !blocks.TryGetValue(~a, out int k))
                    {
                        k = Reserve(2);
                        if (a >= 0) blocks.Add(~a, k);
                        var (header, bits) = Cell.MakeFloat(
                            Cell.DecodeFloat(c, engine.GetHeap(c.FloatPairedIndex)), k + 1);
                        At(k) = header;
                        At(k + 1) = bits;
                    }
                    At(dest) = Cell.Ref(k);
                    break;
                }
                case Tag.Pstr:
                {
                    if (a < 0 || !blocks.TryGetValue(~a, out int k))
                    {
                        int length = c.AsPstrLength;
                        int units = (length + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
                        k = Reserve(1 + units + 1);
                        if (a >= 0) blocks.Add(~a, k);
                        At(k) = Cell.Pstr(length, k + 1, 0, c.AsPstrKind, c.AsPstrIsAstral);
                        CopyCodeUnits(engine, c, k + 1, units);
                        int tail = engine.GetPstrTailIndexOf(c);
                        work.Add((engine.GetHeap(tail), tail, k + 1 + units));
                    }
                    At(dest) = Cell.Ref(k);
                    break;
                }
                case Tag.BigInt:
                    Payload(dest, engine.AsBigInt(c), Cell.BigInt);
                    break;
                case Tag.Rational:
                    Payload(dest, engine.AsRational(c), Cell.Rational);
                    break;
                case Tag.Foreign:
                    Payload(dest, engine.AsForeign(c), Cell.Foreign);
                    break;
                default:
                    throw new System.InvalidOperationException($"A {c.Tag} cell is not a term.");
            }
        }
    }

    /// <summary>Copies solution <paramref name="i"/>, added on its own, to the
    /// heap and returns its root value.</summary>
    public Cell Emit(Activation engine, int i)
    {
        int from = _starts![i];
        int to = i + 1 < _count ? _starts[i + 1] : _length;
        int at = engine.AllocateHeap(to - from);
        CopyOut(engine, from, to, at - from);
        if (_payloadSites is { } sites)
            RemakePayloads(engine, _payloadFrom![i],
                i + 1 < _count ? _payloadFrom[i + 1] : sites.Count, at - from);
        return engine.GetHeap(at);
    }

    /// <summary>Copies the findall solutions to the heap in one block, which is the
    /// list of them, and returns the list. Its tail is <c>[]</c>, or with
    /// <paramref name="openTail"/> a fresh variable, returned in
    /// <paramref name="tail"/>.</summary>
    public Cell EmitList(Activation engine, bool openTail, out Cell tail)
    {
        int total = _length + (openTail ? 1 : 0);
        if (total == 0)
        {
            tail = Cell.Atom(AtomTable.EmptyListId);
            return tail;
        }
        int b = engine.AllocateHeap(total);
        CopyOut(engine, 0, _length, b);
        if (_payloadSites is { } sites) RemakePayloads(engine, 0, sites.Count, b);
        if (openTail)
        {
            tail = Cell.Ref(b + _length);
            engine.SetHeap(b + _length, tail);
        }
        else tail = Cell.Atom(AtomTable.EmptyListId);
        if (_count == 0) return tail;
        engine.SetHeap(b + _lastPair + 1, tail);
        return Cell.Lis(b);
    }

    private ref Cell At(int i)
    {
        if (i < ChunkSize) return ref _first[i];
        int j = i - ChunkSize;
        return ref _chunks![j >> ChunkBits][j & (ChunkSize - 1)];
    }

    /// <summary>The part cell <paramref name="i"/> is in, and its offset there.</summary>
    private int Locate(int i, out Cell[] part)
    {
        if (i < ChunkSize)
        {
            part = _first;
            return i;
        }
        int j = i - ChunkSize;
        part = _chunks![j >> ChunkBits];
        return j & (ChunkSize - 1);
    }

    private int Reserve(int n)
    {
        int start = _length, end = start + n;
        if (end > _first.Length && _first.Length < ChunkSize)
        {
            var old = _first;
            System.Array.Resize(ref _first, System.Math.Min(ChunkSize, System.Math.Max(end, _first.Length * 2)));
            if (ReferenceEquals(_lastTail, old)) _lastTail = _first;
        }
        if (end > ChunkSize)
        {
            _chunks ??= new List<Cell[]>();
            while (ChunkSize * (1 + _chunks.Count) < end) _chunks.Add(new Cell[ChunkSize]);
        }
        _length = end;
        return start;
    }

    /// <summary>Copies cells <paramref name="from"/> to <paramref name="to"/> to
    /// the heap where <paramref name="delta"/> puts them, every index in them
    /// moved by it.</summary>
    private void CopyOut(Activation engine, int from, int to, int delta)
    {
        for (int i = from; i < to;)
        {
            Cell[] part;
            int offset;
            if (i < ChunkSize)
            {
                part = _first;
                offset = i;
            }
            else
            {
                int j = i - ChunkSize;
                part = _chunks![j >> ChunkBits];
                offset = j & (ChunkSize - 1);
            }
            int n = System.Math.Min(ChunkSize - offset, to - i);
            engine.CopyRelocated(new System.ReadOnlySpan<Cell>(part, offset, n), i + delta, delta);
            i += n;
        }
    }

    /// <summary>The argument at heap <paramref name="addr"/> into cell
    /// <paramref name="dest"/>: written now when it is an atom or an integer, the
    /// most common leaf, and left to the walk otherwise.</summary>
    private void Next(Activation engine, List<(Cell, int, int)> work, int addr, int dest)
    {
        Cell c = engine.GetHeap(addr);
        Cell v = c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
        if (v.Tag is Tag.Atom or Tag.Int) At(dest) = v;
        else work.Add((c, addr, dest));
    }

    private void Variable(AddressMap vars, int addr, int dest)
    {
        if (vars.TryGetValue(addr, out int home))
        {
            At(dest) = Cell.Ref(home);
            return;
        }
        vars.Add(addr, dest);
        At(dest) = Cell.Ref(dest);
    }

    private void Payload(int dest, object? value, System.Func<int, Cell> cell)
    {
        if (_payloadSites is null)
        {
            _payloadSites = new List<int>();
            _payloads = new List<object?>();
            // Every solution so far has none: each starts at site 0.
            if (_starts is not null) _payloadFrom = new int[_starts.Length];
        }
        At(dest) = cell(_payloads!.Count);
        _payloads.Add(value);
        _payloadSites.Add(dest);
    }

    /// <summary>The code units of the packed string <paramref name="header"/>
    /// names, packed from the start of <paramref name="at"/>'s cells: a header
    /// past the start of its buffer (the tail of another string) begins mid-cell.</summary>
    private void CopyCodeUnits(Activation engine, Cell header, int at, int cells)
    {
        int buffer = header.AsPstrBufferIndex;
        int offset = header.AsPstrOffset;
        if (offset == 0)
        {
            for (int j = 0; j < cells; j++) At(at + j) = engine.GetHeap(buffer + j);
            return;
        }
        int length = header.AsPstrLength;
        int Unit(int i) => i < length
            ? engine.GetHeap(buffer + (offset + i) / Cell.PstrCodeUnitsPerBuffer)
                .AsPstrCodeUnit((offset + i) % Cell.PstrCodeUnitsPerBuffer)
            : 0;
        for (int j = 0; j < cells; j++)
        {
            int u = j * Cell.PstrCodeUnitsPerBuffer;
            At(at + j) = Cell.PstrBuffer(Unit(u), Unit(u + 1), Unit(u + 2));
        }
    }

    /// <summary>Makes the side-table values of sites <paramref name="from"/> to
    /// <paramref name="to"/> again on <paramref name="engine"/>, over the cells the
    /// copy at <paramref name="delta"/> put there.</summary>
    private void RemakePayloads(Activation engine, int from, int to, int delta)
    {
        var sites = _payloadSites!;
        for (int j = from; j < to; j++)
        {
            int site = sites[j];
            object? value = _payloads![(int)At(site).Payload];
            Cell made = At(site).Tag switch
            {
                Tag.BigInt => engine.MakeBigInt((BigInteger)value!),
                Tag.Rational => engine.MakeRational((Rational)value!),
                _ => engine.MakeForeign(value),
            };
            engine.SetHeap(site + delta, made);
        }
    }
}

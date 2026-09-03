using System.Collections.Generic;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// A backtrack-safe <em>cell image</em> of one findall solution.
///
/// <para><c>findall/3</c> enumerates its goal under a <c>fail</c>-driven loop,
/// so each recorded solution must survive the backtracking that reclaims the
/// WAM heap between solutions. The mechanism did this by materialising
/// each solution to a managed <see cref="Term"/> AST (a GC object graph, off the
/// heap) and re-materialising it back to cells at collect — a heap→AST→heap
/// round-trip that allocates one managed node per term node (measured ~264 B for
/// a small compound solution).</para>
///
/// <para>This snapshots the solution straight into a compact <see cref="Cell"/>[]
/// — a self-contained relative heap image, base-0 addressing — with no
/// per-node managed object, then re-emits it onto the heap at collect by a block
/// copy with a single additive shift. The copy mirrors
/// <see cref="HeapTermCopy"/>'s layout exactly (ADR-017 STR indirection, fresh
/// vars, DAG / cycle sharing via a struct map, iterative list spine) so the
/// re-emitted term is structurally identical to the AST path's.</para>
///
/// <para>A solution containing a <em>value leaf</em> — FLOAT / BIGINT / STRING /
/// PSTR (side-table / buffer payloads) or a FOREIGN cell — falls back to the AST
/// path (<see cref="TrySnapshotRegister"/> returns <c>null</c>): those payloads
/// are not blittable into a flat cell image, and such templates are rare. The
/// common atom / int / var / compound / list solution takes the fast path.</para>
/// </summary>
internal static class FindallSnapshot
{
    /// <summary>Thrown internally when the walk meets a value leaf, unwinding to
    /// <see cref="TrySnapshotRegister"/> which returns <c>null</c> so the caller
    /// records an AST term instead. Not visible outside this file.</summary>
    private sealed class ValueLeafException : System.Exception { }
    private static readonly ValueLeafException ValueLeaf = new();

    /// <summary>Snapshots the value in register <paramref name="regIdx"/> to a
    /// relative cell image, or returns <c>null</c> if it holds a value leaf and
    /// the caller should fall back to the AST path. The image's slot 0 is the
    /// root value cell.</summary>
    public static Cell[]? TrySnapshotRegister(Activation engine, int regIdx)
    {
        // Pooled, clear-on-use scratch (snapshots never nest). Only the ToArray
        // below allocates per solution — the detached backtrack-safe image.
        List<Cell> cells = engine.FindallSnapCells ??= new List<Cell>(16);
        Dictionary<int, int> varMap = engine.FindallSnapVarMap ??= new Dictionary<int, int>();
        Dictionary<int, int> structMap = engine.FindallSnapStructMap ??= new Dictionary<int, int>();
        cells.Clear();
        varMap.Clear();
        structMap.Clear();
        cells.Add(default);   // reserve slot 0 for the root value cell
        try
        {
            cells[0] = CopyValue(engine, engine.GetRegister(regIdx), cells, varMap, structMap);
        }
        catch (ValueLeafException)
        {
            return null;
        }
        return cells.ToArray();
    }

    /// <summary>Re-emits a snapshot onto <paramref name="engine"/>'s heap and
    /// returns the root value cell. A block copy: every cell carrying a heap index is
    /// shifted by the allocation base, so the image's relative addressing
    /// becomes absolute. Fresh vars (self-referential REFs) stay unbound.</summary>
    public static Cell EmitSnapshot(Activation engine, Cell[] snap)
    {
        int b = engine.AllocateHeap(snap.Length);
        for (int i = 0; i < snap.Length; i++)
            engine.SetHeap(b + i, Shift(snap[i], b));
        return Shift(snap[0], b);
    }

    private static Cell Shift(Cell c, int delta) => c.Tag switch
    {
        Tag.Ref => Cell.Ref(c.AsHeapIndex + delta),
        Tag.Str => Cell.Str(c.AsHeapIndex + delta),
        Tag.Lis => Cell.Lis(c.AsHeapIndex + delta),
        _ => c,   // Atom / Int / Functor carry no heap index
    };

    private static int Reserve(List<Cell> cells, int n)
    {
        int start = cells.Count;
        for (int i = 0; i < n; i++) cells.Add(default);
        return start;
    }

    /// <summary>Copies a register / argument value cell, appending any
    /// sub-structure to <paramref name="cells"/> and returning the value cell
    /// (relative addressing). Mirrors <see cref="HeapTermCopy.CopyRegisterValue"/>.</summary>
    private static Cell CopyValue(Activation engine, Cell rc,
        List<Cell> cells, Dictionary<int, int> varMap, Dictionary<int, int> structMap)
    {
        switch (rc.Tag)
        {
            case Tag.Ref: return CopyAt(engine, rc.AsHeapIndex, cells, varMap, structMap);
            case Tag.Str: return CopyStr(engine, rc.AsHeapIndex, cells, varMap, structMap);
            case Tag.Lis: return CopyLis(engine, rc.AsHeapIndex, cells, varMap, structMap);
            case Tag.Atom:
            case Tag.Int:
                return rc;
            default:
                // A bare FLOAT/BIGINT/PSTR/FOREIGN/ATTVAR register cell — value
                // leaf (or, for ATTVAR, a var reached without a heap address to
                // key off). Stage into one heap slot so CopyAt can deref it; a
                // real value leaf there throws → fallback.
                int tmp = engine.AllocateHeap(1);
                engine.SetHeap(tmp, rc);
                return CopyAt(engine, tmp, cells, varMap, structMap);
        }
    }

    private static Cell CopyAt(Activation engine, int addr,
        List<Cell> cells, Dictionary<int, int> varMap, Dictionary<int, int> structMap)
    {
        int a = engine.Deref(addr);
        Cell c = engine.GetHeap(a);
        switch (c.Tag)
        {
            case Tag.Ref:
            case Tag.AttVar:
                if (varMap.TryGetValue(a, out int existing)) return Cell.Ref(existing);
                int slot = Reserve(cells, 1);
                cells[slot] = Cell.Ref(slot);   // self-ref = unbound, relative
                varMap[a] = slot;
                return Cell.Ref(slot);
            case Tag.Atom:
            case Tag.Int:
                return c;
            case Tag.Float:
            case Tag.BigInt:
            case Tag.Rational:
            case Tag.Pstr:
            case Tag.Foreign:
                // A snapshot outlives the heap state it was taken from, so
                // unlike copy_term it cannot share a packed list's buffer.
                throw ValueLeaf;   // fall back to the AST path
            case Tag.Str:
                return CopyStr(engine, c.AsHeapIndex, cells, varMap, structMap);
            case Tag.Lis:
                return CopyLis(engine, c.AsHeapIndex, cells, varMap, structMap);
            default:
                throw ValueLeaf;
        }
    }

    /// <summary><paramref name="fAddr"/> is the source FUNCTOR cell address.
    /// Mirrors <see cref="HeapTermCopy.CopyStr"/>: image slot layout
    /// [STR(base+1)][Functor][arg0..], value cell REF(base).</summary>
    private static Cell CopyStr(Activation engine, int fAddr,
        List<Cell> cells, Dictionary<int, int> varMap, Dictionary<int, int> structMap)
    {
        if (structMap.TryGetValue(fAddr, out int cached)) return Cell.Ref(cached);
        Cell fcell = engine.GetHeap(fAddr);
        var (_, arity) = FunctorTable.Lookup(fcell.AsFunctorId);
        int baseIdx = Reserve(cells, 2 + arity);
        structMap[fAddr] = baseIdx;   // register BEFORE recursing — cycle / DAG safety
        cells[baseIdx] = Cell.Str(baseIdx + 1);
        cells[baseIdx + 1] = Cell.Functor(fcell.AsFunctorId);
        for (int i = 0; i < arity; i++)
            cells[baseIdx + 2 + i] = CopyAt(engine, fAddr + 1 + i, cells, varMap, structMap);
        return Cell.Ref(baseIdx);
    }

    /// <summary><paramref name="firstHead"/> is the source cons head-cell
    /// address. Mirrors <see cref="HeapTermCopy.CopyLis"/> — iterative spine
    /// walk so a long list does not recurse per element.</summary>
    private static Cell CopyLis(Activation engine, int firstHead,
        List<Cell> cells, Dictionary<int, int> varMap, Dictionary<int, int> structMap)
    {
        if (structMap.TryGetValue(firstHead, out int cachedFirst))
            return Cell.Lis(cachedFirst);
        var srcHeads = new List<int>();
        int cur = firstHead;
        int finalTailAddr;
        // A cons whose tail re-enters the spine BEING walked: image slots do
        // not exist yet, so the walk marks each head provisionally (negative
        // = position on this spine; real slots are never negative — slot 0 is
        // the reserved root). Without the mark, `L = [a|L]` re-walks itself
        // until the image list overflows — an engine-killing OOM, not an
        // error a program can see.
        int selfCycleAt = -1;
        while (true)
        {
            structMap[cur] = -1 - srcHeads.Count;
            srcHeads.Add(cur);
            int tailAddr = cur + 1;
            Cell tc = engine.GetHeap(engine.Deref(tailAddr));
            if (tc.Tag == Tag.Lis)
            {
                int nextHead = tc.AsHeapIndex;
                if (structMap.TryGetValue(nextHead, out int seen))
                {
                    if (seen < 0) { selfCycleAt = -1 - seen; finalTailAddr = -1; }
                    else finalTailAddr = tailAddr;
                    break;
                }
                cur = nextHead;
            }
            else { finalTailAddr = tailAddr; break; }
        }
        int n = srcHeads.Count;
        int firstPair = Reserve(cells, 2 * n);
        for (int i = 0; i < n; i++)
            structMap[srcHeads[i]] = firstPair + 2 * i;   // finalize the marks
        Cell tailCell = selfCycleAt >= 0
            ? Cell.Lis(firstPair + 2 * selfCycleAt)
            : CopyAt(engine, finalTailAddr, cells, varMap, structMap);
        for (int i = 0; i < n; i++)
        {
            cells[firstPair + 2 * i] = CopyAt(engine, srcHeads[i], cells, varMap, structMap);
            cells[firstPair + 2 * i + 1] =
                i + 1 < n ? Cell.Lis(firstPair + 2 * (i + 1)) : tailCell;
        }
        return Cell.Lis(firstPair);
    }
}

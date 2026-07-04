using System.Collections.Generic;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Phase 33 I2 — direct heap-to-heap term copy for <c>copy_term/2</c>.
///
/// <para>The previous path went heap → managed AST (<see cref="TermReader"/>)
/// → heap (<see cref="Materializer"/>), allocating a full <see cref="Term"/>
/// tree (one managed object per node + a var-name dictionary) as garbage on
/// every call — measured ~1.3 KB per copy of a small term. This walks the
/// source heap and writes fresh cells straight to the destination, so a copy
/// of a compound / list / atom / int / var costs no managed allocation beyond
/// the two small identity dictionaries.</para>
///
/// <para>Semantics match the AST round-trip exactly:
/// <list type="bullet">
///   <item>unbound REF / ATTVAR → a fresh <em>plain</em> unbound variable
///     (attributes are dropped by <c>copy_term/2</c>, as <see cref="TermReader"/>
///     does — <c>copy_term/3</c> is the attribute-aware entry);</item>
///   <item>variable sharing is preserved (a source var maps to one fresh var
///     via <c>varMap</c>); structure sharing / cycles are preserved and made to
///     terminate by registering a compound / list in <c>structMap</c> BEFORE
///     recursing into it;</item>
///   <item>FLOAT / BIGINT / PSTR leaves — which live in per-engine side tables
///     / buffers — delegate that single node to the proven AST path so a fresh
///     side-table entry is allocated with identical behaviour; they are ground,
///     so no cross-node variable sharing is lost.</item>
/// </list></para>
///
/// <para>The list spine is walked iteratively (Phase-8 chunk 111): a recursive
/// descent down the tail would use one C# stack frame per element and overflow
/// on a long list.</para>
/// </summary>
internal static class HeapTermCopy
{
    /// <summary>Copies the value held in register <paramref name="regIdx"/> to
    /// fresh heap cells and returns the copied value cell.</summary>
    public static Cell CopyRegister(Engine engine, int regIdx)
    {
        // Pool the two identity maps on the engine (clear-on-use), the
        // chunk-432 pattern: the depth guard means only the outermost copy uses
        // the pooled instances; a nested copy (none happens today — copy_term/2
        // does not re-enter itself — but future callers might) allocates fresh.
        engine.CopyTermDepth++;
        try
        {
            Dictionary<int, Cell> varMap, structMap;
            if (engine.CopyTermDepth == 1)
            {
                varMap = engine.CopyVarScratch ??= new Dictionary<int, Cell>();
                varMap.Clear();
                structMap = engine.CopyStructScratch ??= new Dictionary<int, Cell>();
                structMap.Clear();
            }
            else
            {
                varMap = new Dictionary<int, Cell>();
                structMap = new Dictionary<int, Cell>();
            }
            return CopyRegisterValue(engine, engine.GetRegister(regIdx), varMap, structMap);
        }
        finally
        {
            engine.CopyTermDepth--;
        }
    }

    private static Cell CopyRegisterValue(Engine engine, Cell rc,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap)
    {
        switch (rc.Tag)
        {
            case Tag.Ref: return CopyAt(engine, rc.AsHeapIndex, varMap, structMap);
            case Tag.Str: return CopyStr(engine, rc.AsHeapIndex, varMap, structMap);
            case Tag.Lis: return CopyLis(engine, rc.AsHeapIndex, varMap, structMap);
            case Tag.Atom:
            case Tag.Int:
            case Tag.Foreign:
                return rc;
            default:
                // A bare FLOAT/BIGINT/PSTR/ATTVAR register cell has no heap
                // address for the delegating / var branches to key off. In
                // practice these reach a register as a REF to a heap slot (the
                // cases above), so this is a defensive path: stage the cell into
                // one throwaway heap slot so CopyAt has an address to work from.
                int tmp = engine.AllocateHeap(1);
                engine.SetHeap(tmp, rc);
                return CopyAt(engine, tmp, varMap, structMap);
        }
    }

    /// <summary>Copies the value stored at heap slot <paramref name="addr"/>
    /// (dereferencing first).</summary>
    private static Cell CopyAt(Engine engine, int addr,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap)
    {
        int a = engine.Deref(addr);
        Cell c = engine.GetHeap(a);
        switch (c.Tag)
        {
            case Tag.Ref:
            case Tag.AttVar:
                if (varMap.TryGetValue(a, out Cell existing)) return existing;
                Cell fresh = Cell.Ref(engine.AllocateHeapUnbound());
                varMap[a] = fresh;
                return fresh;
            case Tag.Atom:
            case Tag.Int:
            case Tag.Foreign:
                return c;
            case Tag.Float:
            case Tag.BigInt:
            case Tag.Pstr:
                // Ground side-table / buffer leaf: delegate this one node to the
                // proven AST path (allocates a fresh side-table entry, exactly
                // as the old whole-term round-trip did).
                return Materializer.MaterializeAsCell(engine, TermReader.Materialize(engine, a));
            case Tag.Str:
                return CopyStr(engine, c.AsHeapIndex, varMap, structMap);
            case Tag.Lis:
                return CopyLis(engine, c.AsHeapIndex, varMap, structMap);
            default:
                throw new System.NotSupportedException(
                    $"HeapTermCopy does not handle the {c.Tag} tag.");
        }
    }

    /// <summary><paramref name="fAddr"/> is the source FUNCTOR cell address
    /// (functor at fAddr, args at fAddr+1..fAddr+arity).</summary>
    private static Cell CopyStr(Engine engine, int fAddr,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap)
    {
        if (structMap.TryGetValue(fAddr, out Cell cached)) return cached;
        Cell fcell = engine.GetHeap(fAddr);
        var (_, arity) = FunctorTable.Lookup(fcell.AsFunctorId);
        // Mirror Materializer's layout: [Str(base+1)][Functor][arg0..]; the
        // value cell is Ref(base). Reserve up front so the args (which may
        // extend the heap) land at stable slots.
        int baseIdx = engine.AllocateHeap(2 + arity);
        Cell result = Cell.Ref(baseIdx);
        structMap[fAddr] = result;   // register BEFORE recursing — cycle / DAG safety
        engine.SetHeap(baseIdx, Cell.Str(baseIdx + 1));
        engine.SetHeap(baseIdx + 1, Cell.Functor(fcell.AsFunctorId));
        for (int i = 0; i < arity; i++)
            engine.SetHeap(baseIdx + 2 + i, CopyAt(engine, fAddr + 1 + i, varMap, structMap));
        return result;
    }

    /// <summary><paramref name="firstHead"/> is the source cons's head-cell
    /// address (head at firstHead, tail at firstHead+1).</summary>
    private static Cell CopyLis(Engine engine, int firstHead,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap)
    {
        if (structMap.TryGetValue(firstHead, out Cell cached)) return cached;
        // Walk the spine iteratively, collecting each cons's head-cell address
        // and the final (non-LIS) tail slot.
        var srcHeads = new List<int>();
        int cur = firstHead;
        int finalTailAddr;
        while (true)
        {
            srcHeads.Add(cur);
            int tailAddr = cur + 1;
            Cell tc = engine.GetHeap(engine.Deref(tailAddr));
            if (tc.Tag == Tag.Lis)
            {
                int nextHead = tc.AsHeapIndex;
                if (structMap.ContainsKey(nextHead))   // cyclic spine — stop, copy tail as a value
                { finalTailAddr = tailAddr; break; }
                cur = nextHead;
            }
            else { finalTailAddr = tailAddr; break; }
        }
        int n = srcHeads.Count;
        int firstPair = engine.AllocateHeap(2 * n);
        // Register every cons before copying any element — cycle / DAG safety.
        for (int i = 0; i < n; i++)
            structMap[srcHeads[i]] = Cell.Lis(firstPair + 2 * i);
        Cell tailCell = CopyAt(engine, finalTailAddr, varMap, structMap);
        for (int i = 0; i < n; i++)
        {
            engine.SetHeap(firstPair + 2 * i, CopyAt(engine, srcHeads[i], varMap, structMap));
            engine.SetHeap(firstPair + 2 * i + 1,
                i + 1 < n ? Cell.Lis(firstPair + 2 * (i + 1)) : tailCell);
        }
        return Cell.Lis(firstPair);
    }
}

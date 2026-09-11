using System.Collections.Generic;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// direct heap-to-heap term copy for <c>copy_term/2</c>.
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
/// <para>The list spine is walked iteratively: a recursive
/// descent down the tail would use one C# stack frame per element and overflow
/// on a long list.</para>
/// </summary>
internal static class HeapTermCopy
{
    /// <summary>Copies the value held in register <paramref name="regIdx"/> to
    /// fresh heap cells and returns the copied value cell.</summary>
    public static Cell CopyRegister(Activation engine, int regIdx)
    {
        // Pool the two identity maps on the engine (clear-on-use), the
        // pattern: the depth guard means only the outermost copy uses
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

    /// <summary>C#-recursion depth past which a nested copy is put on a WORK
    /// LIST instead of descending. How deep a term nests is the program's
    /// choice, and a .NET stack overflow cannot be caught: it takes the
    /// process down. Recursive while the depth is known to be safe, an
    /// explicit stack past that.</summary>
    private const int CopyRecursionLimit = 512;

    /// <summary>Copies the value at <paramref name="src"/> into the reserved
    /// slot <paramref name="dst"/>, now or later. Later is sound because every
    /// destination is reserved BEFORE anything is copied into it and both
    /// identity maps are registered before descending, so a cycle or a shared
    /// subterm resolves the same whichever order the pieces are filled
    /// in.</summary>
    private static void CopyInto(Activation engine, int src, int dst, int depth,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap,
        List<long> deferred)
    {
        if (depth < CopyRecursionLimit)
        {
            engine.SetHeap(dst, CopyAt(engine, src, depth, varMap, structMap, deferred));
            return;
        }
        deferred.Add(((long)src << 32) | (uint)dst);
    }

    /// <summary>Fills in every slot the copy put off, and the slots those put
    /// off in turn, until none is left.</summary>
    private static void Drain(Activation engine,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap,
        List<long> deferred)
    {
        while (deferred.Count > 0)
        {
            long pending = deferred[^1];
            deferred.RemoveAt(deferred.Count - 1);
            CopyInto(engine, (int)(pending >> 32), (int)(uint)pending, 0,
                     varMap, structMap, deferred);
        }
    }

    private static Cell CopyRegisterValue(Activation engine, Cell rc,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap)
    {
        var deferred = new List<long>();
        Cell copied = CopyRegisterValue(engine, rc, varMap, structMap, deferred);
        Drain(engine, varMap, structMap, deferred);
        return copied;
    }

    private static Cell CopyRegisterValue(Activation engine, Cell rc,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap,
        List<long> deferred)
    {
        switch (rc.Tag)
        {
            case Tag.Ref: return CopyAt(engine, rc.AsHeapIndex, 0, varMap, structMap, deferred);
            case Tag.Str: return CopyStr(engine, rc.AsHeapIndex, 0, varMap, structMap, deferred);
            case Tag.Lis: return CopyLis(engine, rc.AsHeapIndex, 0, varMap, structMap, deferred);
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
                return CopyAt(engine, tmp, 0, varMap, structMap, deferred);
        }
    }

    /// <summary>Copies the value stored at heap slot <paramref name="addr"/>
    /// (dereferencing first).</summary>
    private static Cell CopyAt(Activation engine, int addr, int depth,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap,
        List<long> deferred)
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
            case Tag.Pstr:
                // A COMPLETE packed list holds no variables and is never mutated
                // in place, so the copy can be the cell itself — the same
                // structure sharing an atom gets. That makes copy_term/2 and
                // assertz of a megabyte of text cost one cell instead of a
                // materialise-and-rebuild of 2n+1. A partial one has a variable
                // in its tail and has to go the long way.
                if (engine.PstrFinalTailCell(c) is { Tag: Tag.Atom } fin
                    && fin.AsAtomId == AtomTable.EmptyListId)
                    return c;
                goto case Tag.Float;
            case Tag.Float:
            case Tag.BigInt:
            case Tag.Rational:
                // Ground side-table / buffer leaf: delegate this one node to the
                // proven AST path (allocates a fresh side-table entry, exactly
                // as the old whole-term round-trip did).
                return Materializer.MaterializeAsCell(engine, TermReader.Materialize(engine, a));
            case Tag.Str:
                return CopyStr(engine, c.AsHeapIndex, depth, varMap, structMap, deferred);
            case Tag.Lis:
                return CopyLis(engine, c.AsHeapIndex, depth, varMap, structMap, deferred);
            default:
                throw new System.NotSupportedException(
                    $"HeapTermCopy does not handle the {c.Tag} tag.");
        }
    }

    /// <summary><paramref name="fAddr"/> is the source FUNCTOR cell address
    /// (functor at fAddr, args at fAddr+1..fAddr+arity).</summary>
    private static Cell CopyStr(Activation engine, int fAddr, int depth,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap,
        List<long> deferred)
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
            CopyInto(engine, fAddr + 1 + i, baseIdx + 2 + i, depth + 1,
                     varMap, structMap, deferred);
        return result;
    }

    /// <summary><paramref name="firstHead"/> is the source cons's head-cell
    /// address (head at firstHead, tail at firstHead+1).</summary>
    private static Cell CopyLis(Activation engine, int firstHead, int depth,
        Dictionary<int, Cell> varMap, Dictionary<int, Cell> structMap,
        List<long> deferred)
    {
        if (structMap.TryGetValue(firstHead, out Cell cached)) return cached;
        // Walk the spine iteratively, collecting each cons's head-cell address
        // and the final (non-LIS) tail slot.
        var srcHeads = new List<int>();
        // walked: the spine cells of THIS walk. structMap only knows cells
        // an outer copy registered — registration for this spine happens
        // after the walk — so a spine that cycles back into ITSELF
        // (L = [a|L]) revisited nothing in structMap and walked forever,
        // growing srcHeads to OOM.
        var walked = new HashSet<int>();
        int cur = firstHead;
        int finalTailAddr;
        while (true)
        {
            srcHeads.Add(cur);
            walked.Add(cur);
            int tailAddr = cur + 1;
            Cell tc = engine.GetHeap(engine.Deref(tailAddr));
            if (tc.Tag == Tag.Lis)
            {
                int nextHead = tc.AsHeapIndex;
                if (structMap.ContainsKey(nextHead) || walked.Contains(nextHead))
                { finalTailAddr = tailAddr; break; }   // cyclic spine — copy tail as a value
                cur = nextHead;
            }
            else { finalTailAddr = tailAddr; break; }
        }
        int n = srcHeads.Count;
        int firstPair = engine.AllocateHeap(2 * n);
        // Register every cons before copying any element — cycle / DAG safety.
        for (int i = 0; i < n; i++)
            structMap[srcHeads[i]] = Cell.Lis(firstPair + 2 * i);
        // The tail goes first, as it did when it was one expression: the
        // order decides which fresh cells land at which addresses, and a
        // variable's address is what the top level prints for it.
        CopyInto(engine, finalTailAddr, firstPair + 2 * (n - 1) + 1, depth + 1,
                 varMap, structMap, deferred);
        for (int i = 0; i < n; i++)
        {
            CopyInto(engine, srcHeads[i], firstPair + 2 * i, depth + 1,
                     varMap, structMap, deferred);
            if (i + 1 < n)
                engine.SetHeap(firstPair + 2 * i + 1, Cell.Lis(firstPair + 2 * (i + 1)));
        }
        return Cell.Lis(firstPair);
    }
}

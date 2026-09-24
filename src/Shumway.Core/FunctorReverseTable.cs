namespace Shumway.Core;

/// <summary>(atom, arity) to functor id, in a form a compiled wasm module can
/// read: an open-addressed table of i64 pairs.
///
/// <para>The functor mirror answers the other direction -- a functor id gives
/// a name and an arity -- which is all a module needs to take a term APART.
/// Putting one together asks the reverse, and that is why <c>=../2</c>'s
/// composing mode stayed the host's: the module had a name and a count and no
/// way to turn them into the id a Str cell carries.</para>
///
/// <para>Unlike every other image here it needs NO funnel. The functor table
/// is append-only and its entries never change, so a row is right forever
/// once written and the only thing that can be missing is a functor nobody
/// has interned yet -- which a module answers by declining, because interning
/// one is allocation the host owns.</para>
///
/// <para>Row layout, two i64 per slot: key ((atom + 1) &lt;&lt; 32) |
/// (uint)arity, then the id. Key 0 is EMPTY and ends a probe; there are no
/// tombstones, because nothing is ever removed. The +1 keeps atom 0
/// distinguishable from an empty slot.</para></summary>
public static class FunctorReverseTable
{
    private static long[] _rows = new long[1024];
    private static int _mask = 511;
    private static int _filled;
    private static readonly object Lock = new();

    /// <summary>The rows, for the world that stages them. REPLACED on
    /// growth, so a caller re-reads it per staging.</summary>
    public static long[] Rows { get { lock (Lock) { Fill(); return _rows; } } }

    public static int Mask { get { lock (Lock) { Fill(); return _mask; } } }

    /// <summary>The probe's starting slot. The module recomputes this exact
    /// function, so changing it means changing both.</summary>
    public static int Hash(int atomId, int arity, int mask)
    {
        uint h = (uint)atomId * 2654435761u + (uint)arity * 2246822519u;
        h ^= h >> 15;
        return (int)(h & (uint)mask);
    }

    public static long Key(int atomId, int arity)
        => ((long)(atomId + 1) << 32) | (uint)arity;

    /// <summary>The id for a name and an arity, or -1. The host reads the
    /// functor table itself; this is for the cross-check and its tests.
    /// </summary>
    public static int Lookup(int atomId, int arity)
    {
        lock (Lock)
        {
            Fill();
            long key = Key(atomId, arity);
            int slot = Hash(atomId, arity, _mask);
            for (int n = _mask + 1; n > 0; n--)
            {
                long k = _rows[slot * 2];
                if (k == 0) return -1;
                if (k == key) return (int)_rows[slot * 2 + 1];
                slot = (slot + 1) & _mask;
            }
            return -1;
        }
    }

    private static void Fill()
    {
        int limit = FunctorTable.IdLimit;
        if (limit <= _filled) return;
        // Half full at most, which is where an open-addressed probe stays
        // short. Rebuilding replays every id rather than the new ones,
        // because the slots move.
        if ((limit + 1) * 4 > _rows.Length)
        {
            int slots = 512;
            while (slots < (limit + 1) * 2) slots *= 2;
            _rows = new long[slots * 2];
            _mask = slots - 1;
            _filled = 0;
        }
        for (int id = _filled; id < limit; id++)
        {
            var (atomId, arity) = FunctorTable.Lookup(id);
            Insert(Key(atomId, arity), Hash(atomId, arity, _mask), id);
        }
        _filled = limit;
    }

    private static void Insert(long key, int slot, int id)
    {
        while (true)
        {
            long k = _rows[slot * 2];
            // An id already there wins: two ids for one (name, arity) cannot
            // happen, and if it ever did the FIRST is the one every existing
            // term already carries.
            if (k == key) return;
            if (k == 0)
            {
                _rows[slot * 2] = key;
                _rows[slot * 2 + 1] = id;
                return;
            }
            slot = (slot + 1) & _mask;
        }
    }
}

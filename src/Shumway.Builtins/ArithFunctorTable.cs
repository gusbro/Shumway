namespace Shumway.Builtins;

using Shumway.Core;

/// <summary>Which functors an arithmetic evaluation may apply, in a form a
/// compiled wasm module can read: one i32 per functor id.
///
/// <para>A module cannot compare strings, so "is this functor a plus" has to
/// arrive as a number. The table is DERIVED from the evaluator's own name
/// resolvers rather than restated, because a second list of operator names
/// is a second thing to get wrong -- and the failure would be silent, a
/// module computing with the wrong operator rather than declining.</para>
///
/// <para>Row: 0 means not evaluable, otherwise (arity &lt;&lt; 8) | op, where
/// the op is the evaluator's own <c>BinOp</c> or <c>UnOp</c> code. Which of
/// those the module actually IMPLEMENTS is the module's business: it applies
/// what it can and declines the rest, and declining is always sound because
/// the engine then evaluates the whole expression itself.</para>
///
/// <para>Grows with the functor table and is only ever appended to, so a
/// world re-copies the tail rather than the whole thing.</para></summary>
public static class ArithFunctorTable
{
    private static int[] _rows = new int[256];
    private static int _filled;
    private static readonly object Lock = new();

    /// <summary>Rows for every functor id interned so far. The array is
    /// REPLACED on growth, so a caller re-reads it per staging.</summary>
    public static int[] Rows { get { lock (Lock) { Fill(); return _rows; } } }

    /// <summary>How many of them are live.</summary>
    public static int Length { get { lock (Lock) { Fill(); return _filled; } } }

    /// <summary>The row for one functor, for tests and for the host side of
    /// anything that wants to agree with the module.</summary>
    public static int RowOf(int functorId)
    {
        lock (Lock)
        {
            Fill();
            return (uint)functorId < (uint)_filled ? _rows[functorId] : 0;
        }
    }

    public static int EncodeBinary(int op) => (2 << 8) | (op & 0xFF);
    public static int EncodeUnary(int op) => (1 << 8) | (op & 0xFF);

    private static void Fill()
    {
        int limit = FunctorTable.IdLimit;
        if (limit <= _filled) return;
        if (limit > _rows.Length)
        {
            int n = _rows.Length;
            while (n < limit) n *= 2;
            System.Array.Resize(ref _rows, n);
        }
        for (int id = _filled; id < limit; id++)
        {
            var (atomId, arity) = FunctorTable.Lookup(id);
            string? name = AtomTable.GetById(atomId)?.Name;
            _rows[id] = name is null ? 0 : Resolve(name, arity);
        }
        _filled = limit;
    }

    private static int Resolve(string name, int arity)
    {
        if (arity == 2
            && ArithmeticEvaluator.TryBinOp(name, out var bop))
            return EncodeBinary((int)bop);
        if (arity == 1
            && ArithmeticEvaluator.TryUnOp(name, out var uop))
            return EncodeUnary((int)uop);
        return 0;
    }
}

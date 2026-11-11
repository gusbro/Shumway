namespace Shumway.Builtins;

using Shumway.Core;

/// <summary>Which functors an arithmetic evaluation may apply, in a form a
/// compiled wasm module can read: one i32 per functor id.
///
/// <para>A module cannot compare strings, so "is this functor a plus" has to
/// arrive as a number. The table is derived from the evaluator's own name
/// resolvers rather than restated, because a second list of operator names
/// is a second thing to get wrong -- and the failure would be silent, a
/// module computing with the wrong operator rather than declining.</para>
///
/// <para>Row: 0 means not evaluable, otherwise (arity &lt;&lt; 8) | op, where
/// the op is the evaluator's own <c>BinOp</c> or <c>UnOp</c> code. Which of
/// those the module actually implements is the module's business: it applies
/// what it can and declines the rest, and declining is always sound because
/// the engine then evaluates the whole expression itself.</para>
///
/// <para>Grows with the functor table and is only ever appended to, so a
/// world re-copies the tail rather than the whole thing.</para></summary>
public static class ArithFunctorTable
{
    private static int[] _rows = new int[256];
    // The atom each row was written for: a dead atom's id goes to another name,
    // whose functors take the old entries (ADR-003), so a row is written again
    // when its functor's atom changed. A copy a module took keeps the old row,
    // which is sound: a row other than 0 names a permanent atom, never replaced,
    // and on a 0 the module declines and the engine evaluates.
    private static Atom?[] _rowAtoms = new Atom?[256];
    private static int _filled;
    private static readonly object Lock = new();

    // Rows and fill as one object, so a reader without the lock never takes a
    // length from after a growth with the array from before it.
    private sealed record Image(int[] Rows, int Filled);
    private static Image _image = new(_rows, 0);

    /// <summary>Rows for every functor id interned so far, and how many are
    /// live, from the same fill. The array is replaced on growth, so a caller
    /// re-reads it per staging; the lock is taken only when a functor was
    /// interned since the last fill.</summary>
    public static int[] Current(out int length)
    {
        Image image = Volatile.Read(ref _image);
        if (image.Filled < FunctorTable.IdLimit)
        {
            lock (Lock) { Fill(); image = _image; }
        }
        length = image.Filled;
        return image.Rows;
    }

    public static int[] Rows => Current(out _);

    /// <summary>How many of them are live.</summary>
    public static int Length
    {
        get { Current(out int length); return length; }
    }

    /// <summary>The row for one functor, for tests and for the host side of
    /// anything that wants to agree with the module.</summary>
    public static int RowOf(int functorId)
    {
        lock (Lock)
        {
            Fill();
            if ((uint)functorId >= (uint)_filled) return 0;
            var (atomId, arity) = FunctorTable.Lookup(functorId);
            var atom = AtomTable.ProbeById(atomId);
            if (!ReferenceEquals(atom, _rowAtoms[functorId]))
            {
                _rows[functorId] = atom is null ? 0 : RowFor(atom, arity);
                _rowAtoms[functorId] = atom;
            }
            return _rows[functorId];
        }
    }

    /// <summary>Whether <paramref name="name"/>/<paramref name="arity"/> is an
    /// arithmetic function the table gives a row: code that names one interns
    /// the name permanent, as the engine's own names are (ADR-003), so its row
    /// is written for an atom that never dies.</summary>
    public static bool IsEvaluable(string name, int arity) => Resolve(name, arity) != 0;

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
            System.Array.Resize(ref _rowAtoms, n);
        }
        for (int id = _filled; id < limit; id++)
        {
            var (atomId, arity) = FunctorTable.Lookup(id);
            var atom = AtomTable.ProbeById(atomId);
            _rows[id] = atom is null ? 0 : RowFor(atom, arity);
            _rowAtoms[id] = atom;
        }
        _filled = limit;
        Volatile.Write(ref _image, new Image(_rows, limit));
    }

    // A row stays as written, and a dead atom's id goes to another name (ADR-003):
    // an operation is kept only for a permanent name. An evaluable name is made
    // permanent here, and its row kept only if its atom still has this id.
    private static int RowFor(Atom atom, int arity)
    {
        int row = Resolve(atom.Name, arity);
        if (row == 0 || atom.IsPermanent) return row;
        return AtomTable.Intern(atom.Name, permanent: true).Id == atom.Id ? row : 0;
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

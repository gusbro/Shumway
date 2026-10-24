using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// helpers the source-generated <c>[PrologPredicate]</c>
/// bridges call to shuttle values between Prolog registers and
/// .NET-side <see cref="Term"/>s.
///
/// <para>Public so the generator can emit fully-qualified call sites
/// without forcing the host to import an internal type — the
/// generator runs in the user's compilation, not Shumway.Embedding's.</para>
/// </summary>
public static class RegisterMarshalling
{
    /// <summary>Reads register <paramref name="regIdx"/> as a Prolog
    /// <see cref="Term"/>. the hot interop read: a REF register
    /// materializes from its heap home directly (no throwaway cell), and an
    /// immediate integer / atom becomes its Term with zero heap traffic. Only an
    /// immediate NON-scalar in the register (an ADR-017 inline Str/Lis, a Float /
    /// BigInt / String / Foreign / Pstr id cell) still stages through one
    /// throwaway heap cell so <see cref="TermReader.Materialize"/> can walk it
    /// uniformly.</summary>
    public static Term ReadRegisterAsTerm(Activation engine, int regIdx)
    {
        Cell c = engine.GetRegister(regIdx);
        switch (c.Tag)
        {
            case Tag.Ref:
                // Materialize derefs itself — walk from the heap home, no alloc.
                return TermReader.Materialize(engine, c.AsHeapIndex);
            case Tag.Int:
                return new IntTerm(c.AsInt);
            case Tag.Atom:
                // Seed the atom-id cache — we have the id in hand.
                return new AtomTerm(AtomTable.GetById(c.AsAtomId)?.Name ?? "", c.AsAtomId);
        }
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, c);
        return TermReader.Materialize(engine, slot);
    }

    /// <summary>The dereferenced cell a register holds, without
    /// materializing a Term: an unbound register comes back as its self-REF cell.
    /// The zero-allocation primitive for interop call sites that only need a
    /// scalar / Foreign payload.</summary>
    public static Cell DerefRegisterCell(Activation engine, int regIdx)
    {
        Cell c = engine.GetRegister(regIdx);
        if (c.Tag == Tag.Ref)
            return engine.GetHeap(engine.Deref(c.AsHeapIndex));
        return c;
    }

    /// <summary>cell-direct scalar read for a generated
    /// [PrologPredicate] bridge's <c>+</c> parameter: an Int cell yields its
    /// payload with zero allocation (the Term path allocated one IntTerm per
    /// scalar argument per call — measured 120 B/call on a 3-arg foreign).
    /// An unbound register raises the same instantiation_error the bridge
    /// raised; anything else (BigInt, wrong type) falls back to the exact
    /// FromTerm semantics so error text and range behavior are unchanged.</summary>
    public static long ReadInt64Register(Activation engine, PrologEngine host, int regIdx)
    {
        Cell c = DerefRegisterCell(engine, regIdx);
        if (c.Tag == Tag.Int) return c.AsInt;
        if (c.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        return host.FromTerm<long>(ReadRegisterAsTerm(engine, regIdx));
    }

    /// <summary>As <see cref="ReadInt64Register"/> for an <c>int</c> parameter;
    /// an in-range Int cell is zero-allocation, everything else (incl. out of
    /// int range) falls back to FromTerm&lt;int&gt; for exact semantics.</summary>
    public static int ReadInt32Register(Activation engine, PrologEngine host, int regIdx)
    {
        Cell c = DerefRegisterCell(engine, regIdx);
        if (c.Tag == Tag.Int)
        {
            long v = c.AsInt;
            if (v >= int.MinValue && v <= int.MaxValue) return (int)v;
        }
        else if (c.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        return host.FromTerm<int>(ReadRegisterAsTerm(engine, regIdx));
    }

    /// <summary>Materialises <paramref name="term"/> onto the
    /// engine's heap and unifies the resulting cell against
    /// register <paramref name="regIdx"/>. Returns the unification
    /// outcome — the bridge passes it straight back as the
    /// builtin's success / failure value.</summary>
    public static bool UnifyRegisterWithTerm(Activation engine, int regIdx, Term term)
    {
        Cell cell = Materializer.MaterializeAsCell(engine, term);
        return engine.UnifyRegisterWithCell(regIdx, cell);
    }

    // ---- Values read from and written to cells, without Term trees ----
    //
    // A generated bridge converts a value of a shape it knows (a scalar, a
    // string, a list or array of those, a [PrologTerm] type) straight from and
    // to the heap. A reader takes only the shape it expects and says false for
    // anything else; the bridge then converts through a Term tree as it always
    // did, so every error is the one the converters raise.

    /// <summary>Reads the term at <paramref name="cell"/> as a value; false
    /// when it is not the shape this reader takes.</summary>
    public delegate bool CellReader<T>(Activation engine, PrologEngine host, Cell cell, out T value);

    /// <summary>Writes <paramref name="value"/> as a term on the heap and
    /// returns its cell.</summary>
    public delegate Cell CellWriter<in T>(Activation engine, PrologEngine host, T value);

    private static readonly int TrueAtomId = AtomTable.Intern("true", permanent: true).Id;
    private static readonly int FalseAtomId = AtomTable.Intern("false", permanent: true).Id;

    /// <summary>The dereferenced cell <paramref name="cell"/> names.</summary>
    public static Cell DerefCell(Activation engine, Cell cell)
        => cell.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(cell.AsHeapIndex)) : cell;

    public static bool ReadLong(Activation engine, PrologEngine host, Cell cell, out long value)
    {
        Cell c = DerefCell(engine, cell);
        value = c.Tag == Tag.Int ? c.AsInt : 0;
        return c.Tag == Tag.Int;
    }

    public static bool ReadInt(Activation engine, PrologEngine host, Cell cell, out int value)
    {
        Cell c = DerefCell(engine, cell);
        bool ok = c.Tag == Tag.Int && c.AsInt >= int.MinValue && c.AsInt <= int.MaxValue;
        value = ok ? (int)c.AsInt : 0;
        return ok;
    }

    public static bool ReadDouble(Activation engine, PrologEngine host, Cell cell, out double value)
    {
        Cell c = DerefCell(engine, cell);
        switch (c.Tag)
        {
            case Tag.Float:
                value = Cell.DecodeFloat(c, engine.GetHeap(c.FloatPairedIndex));
                return true;
            case Tag.Int:
                value = c.AsInt;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    public static bool ReadBool(Activation engine, PrologEngine host, Cell cell, out bool value)
    {
        Cell c = DerefCell(engine, cell);
        value = c.Tag == Tag.Atom && c.AsAtomId == TrueAtomId;
        return c.Tag == Tag.Atom && (value || c.AsAtomId == FalseAtomId);
    }

    /// <summary>An atom (<c>[]</c> is the empty string), or a packed string
    /// that ends the list.</summary>
    public static bool ReadString(Activation engine, PrologEngine host, Cell cell, out string value)
    {
        Cell c = DerefCell(engine, cell);
        if (c.Tag == Tag.Atom)
        {
            value = c.AsAtomId == AtomTable.EmptyListId
                ? "" : AtomTable.GetById(c.AsAtomId)?.Name ?? "";
            return true;
        }
        if (c.Tag == Tag.Pstr)
        {
            value = engine.ReadPstrChain(c, out Cell tail);
            if (tail.Tag == Tag.Atom && tail.AsAtomId == AtomTable.EmptyListId) return true;
        }
        value = "";
        return false;
    }

    /// <summary>A proper list each of whose elements <paramref name="element"/>
    /// reads.</summary>
    public static bool ReadList<T>(Activation engine, PrologEngine host, Cell cell,
        CellReader<T> element, out List<T> value)
    {
        value = null!;
        var list = new List<T>();
        Cell c = DerefCell(engine, cell);
        // A proper list has fewer pairs than the heap has cells: past that it
        // is cyclic, and the Term way says what that is.
        int guard = engine.HeapTop;
        while (c.Tag == Tag.Lis)
        {
            if (--guard < 0) return false;
            int pair = c.AsHeapIndex;
            if (!element(engine, host, engine.GetHeap(pair), out T item)) return false;
            list.Add(item);
            c = DerefCell(engine, engine.GetHeap(pair + 1));
        }
        if (c.Tag != Tag.Atom || c.AsAtomId != AtomTable.EmptyListId) return false;
        value = list;
        return true;
    }

    public static bool ReadArray<T>(Activation engine, PrologEngine host, Cell cell,
        CellReader<T> element, out T[] value)
    {
        bool ok = ReadList(engine, host, cell, element, out List<T> list);
        value = ok ? list.ToArray() : null!;
        return ok;
    }

    /// <summary>A value of a type no reader takes, through a Term tree: what
    /// the converters return, or the error they raise.</summary>
    public static bool ReadViaTerm<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods)] T>(
        Activation engine, PrologEngine host, Cell cell, out T value)
    {
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, cell);
        value = host.FromTerm<T>(TermReader.Materialize(engine, slot));
        return true;
    }

    public static Cell LongCell(Activation engine, PrologEngine host, long value)
        => value < Cell.MinInt60 || value > Cell.MaxInt60
            ? engine.MakeBigInt(new System.Numerics.BigInteger(value))
            : Cell.Int(value);

    public static Cell IntCell(Activation engine, PrologEngine host, int value) => Cell.Int(value);

    public static Cell DoubleCell(Activation engine, PrologEngine host, double value)
        => Cell.Ref(engine.MakeFloat(value));

    public static Cell BoolCell(Activation engine, PrologEngine host, bool value)
        => Cell.Atom(value ? TrueAtomId : FalseAtomId);

    public static Cell StringCell(Activation engine, PrologEngine host, string value)
        => value is null ? CellViaTerm(engine, host, value!) : Cell.Atom(AtomTable.Intern(value).Id);

    /// <summary>The list of <paramref name="items"/> (<c>[]</c> for null),
    /// each written by <paramref name="element"/>, its pairs contiguous.</summary>
    public static Cell ListCell<T>(Activation engine, PrologEngine host,
        IEnumerable<T>? items, CellWriter<T> element)
    {
        Cell nil = Cell.Atom(AtomTable.EmptyListId);
        if (items is null) return nil;
        IReadOnlyList<T> list = items as IReadOnlyList<T> ?? new List<T>(items);
        int n = list.Count;
        if (n == 0) return nil;
        int h = engine.AllocateHeap(2 * n);
        for (int i = 0; i < n; i++)
        {
            engine.SetHeap(h + 2 * i, element(engine, host, list[i]));
            engine.SetHeap(h + 2 * i + 1, i + 1 < n ? Cell.Lis(h + 2 * i + 2) : nil);
        }
        return Cell.Lis(h);
    }

    /// <summary>The term at <paramref name="cell"/> as a <typeparamref
    /// name="T"/> for the types with a reader that need no generated code
    /// (the scalars, string, and lists and arrays of them); false for any
    /// other type or a term of another shape. For a host-side read, where
    /// no bridge knows the type ahead.</summary>
    public static bool TryReadAs<T>(Activation engine, PrologEngine host, Cell cell, out T value)
    {
        value = default!;
        bool ok;
        object? v;
        if (typeof(T) == typeof(long)) { ok = ReadLong(engine, host, cell, out long x); v = x; }
        else if (typeof(T) == typeof(int)) { ok = ReadInt(engine, host, cell, out int x); v = x; }
        else if (typeof(T) == typeof(double)) { ok = ReadDouble(engine, host, cell, out double x); v = x; }
        else if (typeof(T) == typeof(bool)) { ok = ReadBool(engine, host, cell, out bool x); v = x; }
        else if (typeof(T) == typeof(string)) { ok = ReadString(engine, host, cell, out string x); v = x; }
        else if (typeof(T) == typeof(List<long>)) { ok = ReadList(engine, host, cell, ReadLong, out List<long> x); v = x; }
        else if (typeof(T) == typeof(List<int>)) { ok = ReadList(engine, host, cell, ReadInt, out List<int> x); v = x; }
        else if (typeof(T) == typeof(List<double>)) { ok = ReadList(engine, host, cell, ReadDouble, out List<double> x); v = x; }
        else if (typeof(T) == typeof(List<string>)) { ok = ReadList(engine, host, cell, ReadString, out List<string> x); v = x; }
        else if (typeof(T) == typeof(long[])) { ok = ReadArray(engine, host, cell, ReadLong, out long[] x); v = x; }
        else if (typeof(T) == typeof(int[])) { ok = ReadArray(engine, host, cell, ReadInt, out int[] x); v = x; }
        else if (typeof(T) == typeof(double[])) { ok = ReadArray(engine, host, cell, ReadDouble, out double[] x); v = x; }
        else if (typeof(T) == typeof(string[])) { ok = ReadArray(engine, host, cell, ReadString, out string[] x); v = x; }
        else return false;
        if (ok) value = (T)v!;
        return ok;
    }

    /// <summary>A [PrologTerm] value's cell; null goes the Term way.</summary>
    public static Cell PrologTermCell<T>(Activation engine, PrologEngine host, T value)
        where T : IPrologCell
        => value is null ? CellViaTerm(engine, host, value!) : value.ToPrologCell(engine, host);

    /// <summary>A value of a type no writer takes, through a Term tree.</summary>
    public static Cell CellViaTerm<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods)] T>(
        Activation engine, PrologEngine host, T value)
        => Materializer.MaterializeAsCell(engine, host.ToTerm<T>(value));
}

/// <summary>A type whose values write themselves as terms on the heap: what
/// the <c>[PrologTerm]</c> generator gives the types it covers.</summary>
public interface IPrologCell
{
    /// <summary>Writes this value as a term on <paramref name="engine"/>'s
    /// heap and returns its cell.</summary>
    Cell ToPrologCell(Activation engine, PrologEngine host);
}

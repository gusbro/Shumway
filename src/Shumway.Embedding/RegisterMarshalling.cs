using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Chunk 242 — helpers the source-generated <c>[PrologPredicate]</c>
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
    /// <see cref="Term"/>. Phase 33 A2 — the hot interop read: a REF register
    /// materializes from its heap home directly (no throwaway cell), and an
    /// immediate integer / atom becomes its Term with zero heap traffic. Only an
    /// immediate NON-scalar in the register (an ADR-017 inline Str/Lis, a Float /
    /// BigInt / String / Foreign / Pstr id cell) still stages through one
    /// throwaway heap cell so <see cref="TermReader.Materialize"/> can walk it
    /// uniformly.</summary>
    public static Term ReadRegisterAsTerm(Engine engine, int regIdx)
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
                // Seed the chunk-431 atom-id cache — we have the id in hand.
                return new AtomTerm(AtomTable.GetById(c.AsAtomId)?.Name ?? "", c.AsAtomId);
        }
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, c);
        return TermReader.Materialize(engine, slot);
    }

    /// <summary>Phase 33 A2/A3 — the dereferenced cell a register holds, without
    /// materializing a Term: an unbound register comes back as its self-REF cell.
    /// The zero-allocation primitive for interop call sites that only need a
    /// scalar / Foreign payload.</summary>
    public static Cell DerefRegisterCell(Engine engine, int regIdx)
    {
        Cell c = engine.GetRegister(regIdx);
        if (c.Tag == Tag.Ref)
            return engine.GetHeap(engine.Deref(c.AsHeapIndex));
        return c;
    }

    /// <summary>Phase 33 C3 — cell-direct scalar read for a generated
    /// [PrologPredicate] bridge's <c>+</c> parameter: an Int cell yields its
    /// payload with ZERO allocation (the Term path allocated one IntTerm per
    /// scalar argument per call — measured 120 B/call on a 3-arg foreign).
    /// An unbound register raises the same instantiation_error the bridge
    /// raised; anything else (BigInt, wrong type) falls back to the exact
    /// FromTerm semantics so error text and range behavior are unchanged.</summary>
    public static long ReadInt64Register(Engine engine, PrologEngine host, int regIdx)
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
    public static int ReadInt32Register(Engine engine, PrologEngine host, int regIdx)
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
    public static bool UnifyRegisterWithTerm(Engine engine, int regIdx, Term term)
    {
        Cell cell = Materializer.MaterializeAsCell(engine, term);
        return engine.UnifyRegisterWithCell(regIdx, cell);
    }
}

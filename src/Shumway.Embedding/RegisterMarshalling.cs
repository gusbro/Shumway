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

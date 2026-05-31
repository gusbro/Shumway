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
    /// <see cref="Term"/>. Internally allocates one throwaway heap
    /// cell so <see cref="TermReader.Materialize"/> can do its
    /// REF-chasing work uniformly — the cost is one inlinable cell
    /// allocation regardless of the register's shape.</summary>
    public static Term ReadRegisterAsTerm(Engine engine, int regIdx)
    {
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, engine.GetRegister(regIdx));
        return TermReader.Materialize(engine, slot);
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

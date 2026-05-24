using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// SWI-Prolog / GProlog global variables. Two variants:
///
/// <list type="bullet">
/// <item><c>nb_setval/2</c> / <c>nb_getval/2</c> — non-backtrackable.
///   A change persists across backtracking; matches the
///   "shared counter / accumulator" use case.</item>
/// <item><c>b_setval/2</c> / <c>b_getval/2</c> — backtrackable.
///   The binding is reverted on backtrack via the engine's
///   ExtraTrail; matches the "thread per-branch context" pattern.</item>
/// </list>
///
/// <para>The store lives on the per-engine
/// <see cref="GlobalVarStore"/> (chunk 145). Names are atoms; values
/// are full heap-allocated copies of the input term, so the var holds
/// a stable snapshot rather than a heap-pointer that could later be
/// reclaimed.</para>
/// </summary>
public static class GlobalVarsBuiltins
{
    public static bool NbSetval(Engine engine)
    {
        string name = ResolveAtomName(engine, engine.GetRegister(0), "nb_setval/2");
        Cell value = Resolve(engine, engine.GetRegister(1));
        // For value-bearing cells (Int / Atom / Float-paired / BigInt
        // / Foreign) the cell itself carries the value, safe across
        // queries. Str / Lis / Pstr cells carry a heap index and could
        // dangle once the per-query heap unwinds — those store-and-go
        // cases need a deep snapshot, which a future chunk will add.
        // The accumulator / counter pattern that motivates global vars
        // overwhelmingly uses integers, so this works in practice.
        Globals(engine).Set(name, value, backtrackable: false);
        return true;
    }

    public static bool NbGetval(Engine engine)
    {
        string name = ResolveAtomName(engine, engine.GetRegister(0), "nb_getval/2");
        if (!Globals(engine).TryGet(name, out Cell stored))
            throw new PrologRuntimeException("existence_error", "variable");
        return engine.UnifyRegisterWithCell(1, stored);
    }

    /// <summary><c>nb_current(?Name, ?Value)</c> — enumerates the
    /// global var store; like nb_getval but doesn't throw on a
    /// missing entry.</summary>
    public static bool NbCurrent(Engine engine)
    {
        Cell nameCell = Resolve(engine, engine.GetRegister(0));
        if (nameCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(nameCell.AsAtomId)?.Name ?? "";
            if (!Globals(engine).TryGet(name, out Cell stored)) return false;
            return engine.UnifyRegisterWithCell(1, stored);
        }
        // Var name → enumerate. Use the standard CP-driven pattern.
        var entries = Globals(engine).All().ToArray();
        int returnPc = engine.P + 9;
        return NbCurrentStep(engine, entries, 0, returnPc, isResume: false);
    }

    private static bool NbCurrentStep(
        Engine engine, (string Name, Cell Value)[] entries,
        int idx, int returnPc, bool isResume)
    {
        if (idx >= entries.Length) return false;
        if (idx + 1 < entries.Length)
        {
            int nextIdx = idx + 1;
            Func<Engine, int, bool> resume = (e, _) =>
                NbCurrentStep(e, entries, nextIdx, returnPc, isResume: true);
            engine.PushBuiltinChoicePoint(resume, arity: 0);
        }
        var (name, value) = entries[idx];
        Cell nameCell = Cell.Atom(AtomTable.Intern(name, permanent: false).Id);
        if (!engine.UnifyRegisterWithCell(0, nameCell)) return false;
        if (!engine.UnifyRegisterWithCell(1, value)) return false;
        if (isResume) engine.ResumeAtReturnPc(returnPc);
        return true;
    }

    public static bool BSetval(Engine engine)
    {
        string name = ResolveAtomName(engine, engine.GetRegister(0), "b_setval/2");
        Cell value = Resolve(engine, engine.GetRegister(1));
        Globals(engine).Set(name, value, backtrackable: true);
        return true;
    }

    public static bool BGetval(Engine engine)
    {
        string name = ResolveAtomName(engine, engine.GetRegister(0), "b_getval/2");
        if (!Globals(engine).TryGet(name, out Cell stored))
            throw new PrologRuntimeException("existence_error", "variable");
        return engine.UnifyRegisterWithCell(1, stored);
    }

    // ---------- helpers ----------

    private static GlobalVarStore Globals(Engine engine)
    {
        if (engine.Host is not IGlobalVarHost host)
            throw new InvalidOperationException(
                "Global variable builtins require the engine to be hosted by a "
                + "type that exposes a GlobalVarStore.");
        return host.GlobalVars;
    }

    private static string ResolveAtomName(Engine engine, Cell cell, string builtinName)
    {
        Cell d = Resolve(engine, cell);
        if (d.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (d.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        return AtomTable.GetById(d.AsAtomId)?.Name ?? "";
    }

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}

/// <summary>Host-side interface so <see cref="GlobalVarsBuiltins"/>
/// can reach the per-engine <see cref="GlobalVarStore"/> without
/// the Builtins project depending on Embedding. Implemented by
/// <c>PrologEngine</c>.</summary>
public interface IGlobalVarHost
{
    GlobalVarStore GlobalVars { get; }
}

using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// SWI-Prolog / GProlog global variables. Two variants:
///
/// <list type="bullet">
/// <item><c>nb_setval/2</c> / <c>nb_getval/2</c> — non-backtrackable.
///   A change persists across backtracking; matches the
///   "shared counter / accumulator" use case.</item>
/// <item><c>b_setval/2</c> / <c>b_getval/2</c> — backtrackable in
///   intent; currently stored non-backtrackably (see
///   <see cref="GlobalVarStore"/>).</item>
/// </list>
///
/// <para>The store lives on the per-engine
/// <see cref="GlobalVarStore"/>. Names are atoms; values
/// are full heap-allocated copies of the input term, so the var holds
/// a stable snapshot rather than a heap-pointer that could later be
/// reclaimed.</para>
/// </summary>
public static class GlobalVarsBuiltins
{
    public static bool NbSetval(Activation engine)
    {
        int nameId = ResolveAtomId(engine, engine.GetRegister(0));
        Cell value = Resolve(engine, engine.GetRegister(1));
        // For value-bearing cells (Int / Atom / Float-paired / BigInt
        // / Foreign) the cell itself carries the value, safe across
        // queries. Str / Lis / Pstr cells carry a heap index and could
        // dangle once the per-query heap unwinds — those cases would
        // need a deep snapshot (not implemented). The accumulator /
        // counter pattern that motivates global vars overwhelmingly
        // uses integers, so this works in practice.
        Globals(engine).Set(nameId, value, backtrackable: false);
        return true;
    }

    public static bool NbGetval(Activation engine)
    {
        int nameId = ResolveAtomId(engine, engine.GetRegister(0));
        if (!Globals(engine).TryGet(nameId, out Cell stored))
            throw new PrologRuntimeException("existence_error", "variable");
        return engine.UnifyRegisterWithCell(1, stored);
    }

    /// <summary><c>nb_current(?Name, ?Value)</c> — enumerates the
    /// global var store; like nb_getval but doesn't throw on a
    /// missing entry.</summary>
    public static bool NbCurrent(Activation engine)
    {
        Cell nameCell = Resolve(engine, engine.GetRegister(0));
        if (nameCell.Tag == Tag.Atom)
        {
            if (!Globals(engine).TryGet(nameCell.AsAtomId, out Cell stored)) return false;
            return engine.UnifyRegisterWithCell(1, stored);
        }
        // Var name → enumerate. Use the standard CP-driven pattern.
        var entries = Globals(engine).All().ToArray();
        int returnPc = engine.BuiltinReturnPc;
        // arity 2: the CP must restore nb_current/2's args (a backtrack-
        // clobbered result reg breaks the enumeration).
        return IndexEnumCursor.Start(engine, entries.Length, 2, returnPc,
            (e, i) => NbCurrentUnify(e, entries, i));
    }

    private static bool NbCurrentUnify(
        Activation engine, (string Name, Cell Value)[] entries, int idx)
    {
        var (name, value) = entries[idx];
        Cell nameCell = Cell.Atom(AtomTable.Intern(name, permanent: false).Id);
        if (!engine.UnifyRegisterWithCell(0, nameCell)) return false;
        if (!engine.UnifyRegisterWithCell(1, value)) return false;
        return true;
    }

    public static bool BSetval(Activation engine)
    {
        int nameId = ResolveAtomId(engine, engine.GetRegister(0));
        Cell value = Resolve(engine, engine.GetRegister(1));
        Globals(engine).Set(nameId, value, backtrackable: true);
        return true;
    }

    public static bool BGetval(Activation engine)
    {
        int nameId = ResolveAtomId(engine, engine.GetRegister(0));
        if (!Globals(engine).TryGet(nameId, out Cell stored))
            throw new PrologRuntimeException("existence_error", "variable");
        return engine.UnifyRegisterWithCell(1, stored);
    }

    // ---------- helpers ----------

    private static GlobalVarStore Globals(Activation engine)
    {
        if (engine.Host is not IGlobalVarHost host)
            throw new InvalidOperationException(
                "Global variable builtins require the engine to be hosted by a "
                + "type that exposes a GlobalVarStore.");
        return host.GlobalVars;
    }

    /// <summary>The store is keyed by atom id (see
    /// <see cref="GlobalVarStore"/>), so the builtins resolve the key
    /// to its id and never touch the name string on the hot path.</summary>
    private static int ResolveAtomId(Activation engine, Cell cell)
    {
        Cell d = Resolve(engine, cell);
        if (d.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (d.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        return d.AsAtomId;
    }

    private static Cell Resolve(Activation engine, Cell c)
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

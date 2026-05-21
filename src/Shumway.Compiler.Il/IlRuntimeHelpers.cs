using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Runtime helpers called from IL-emitted code. These are extension
/// points that the IL emission would otherwise have to inline — keeping
/// them as plain static methods reduces IL volume and means each
/// emitted predicate is one indirect call away from the implementation.
///
/// <para>Each helper assumes the embedding layer has populated the
/// matching <see cref="Engine"/> field at query setup time. A null
/// field is a programmer error (a non-embedded engine ran IL it
/// shouldn't have) and surfaces as <see cref="InvalidOperationException"/>
/// rather than a silent miscompile.</para>
/// </summary>
public static class IlRuntimeHelpers
{
    /// <summary>IL <c>get_pstr</c>: build a PSTR cell from the string at
    /// <paramref name="literalId"/> in the engine's current literal pool
    /// and unify it with <c>X[<paramref name="argReg"/>]</c>. Mirrors
    /// what the bytecode interpreter does for the same opcode but
    /// reachable from an IL <c>call</c>.</summary>
    public static bool GetPstr(Engine engine, int literalId, int argReg)
    {
        string s = ResolveStringLiteral(engine, literalId);
        int headerIdx = engine.MakePstr(s);
        return engine.UnifyRegisterWithHeapAt(argReg, headerIdx);
    }

    /// <summary>IL <c>put_pstr</c>: same as <see cref="GetPstr"/> but
    /// writes the resulting REF directly into the register without
    /// unifying (the caller is filling a fresh arg slot).</summary>
    public static void PutPstr(Engine engine, int literalId, int argReg)
    {
        string s = ResolveStringLiteral(engine, literalId);
        int headerIdx = engine.MakePstr(s);
        engine.SetRegister(argReg, Cell.Ref(headerIdx));
    }

    private static string ResolveStringLiteral(Engine engine, int literalId)
    {
        var pool = engine.CurrentStringLiterals
            ?? throw new InvalidOperationException(
                "IL PSTR: engine.CurrentStringLiterals is null. "
                + "The embedding layer must populate it at query setup.");
        if (literalId < 0 || literalId >= pool.Count)
            throw new InvalidOperationException(
                $"IL PSTR: literal id {literalId} is out of range [0, {pool.Count}).");
        return pool[literalId];
    }

    /// <summary>IL <c>Call</c> (chunk 50): runs the callee predicate
    /// identified by <paramref name="calleeFunctorId"/> synchronously
    /// and returns its success / failure. The implementation is a
    /// re-entrant call into the bytecode interpreter — we don't have
    /// the interpreter object on the engine, so we delegate to
    /// <see cref="Engine.IlSubroutineRunner"/> which is wired in at
    /// query-setup time by the embedding layer.
    ///
    /// <para>CanCompile restricts IL Call to callees that don't push
    /// choice points (single-clause body-less facts, or chains thereof),
    /// so the sub-call always runs to completion in one shot. The
    /// helper sets Cp to a sentinel before re-entering the interpreter;
    /// the sentinel causes <c>proceed</c> to exit the inner dispatch
    /// loop back to us.</para></summary>
    public static bool Call(Engine engine, int calleeFunctorId)
    {
        var runner = engine.IlSubroutineRunner
            ?? throw new InvalidOperationException(
                "IL Call: engine.IlSubroutineRunner is null. "
                + "The embedding layer must populate it at query setup.");
        var addresses = engine.CurrentFunctorAddresses
            ?? throw new InvalidOperationException(
                "IL Call: engine.CurrentFunctorAddresses is null.");
        if (!addresses.TryGetValue(calleeFunctorId, out int target))
            throw PrologRuntimeException.UndefinedProcedure(calleeFunctorId);
        return runner(target);
    }

    /// <summary>Meta-CP support (chunk 66): drive one backtrack round
    /// on the bytecode interpreter so an IL Call site's meta-CP can
    /// fetch the next solution from a non-leaf callee. Returns
    /// <c>true</c> when the backtrack landed on an alternative that
    /// proceeded to a halt (success), <c>false</c> when no further
    /// CPs were available.</summary>
    public static bool RunBacktrack(Engine engine)
    {
        var runner = engine.BacktrackRunner
            ?? throw new InvalidOperationException(
                "IL meta-CP: engine.BacktrackRunner is null. "
                + "The embedding layer must populate it at query setup.");
        return runner();
    }

    /// <summary>Meta-CP support (chunk 66): reads back the preCallB
    /// value the meta-CP saved into X[0]. Pushed as
    /// <c>Cell.Int(preCallB)</c> with arity=1 so it survives the CP
    /// frame's restore on pop.</summary>
    public static int ReadPreCallB(Engine engine) =>
        (int)engine.GetRegister(0).AsInt;
}

using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Runtime helpers called from IL-emitted code. These are extension
/// points that the IL emission would otherwise have to inline — keeping
/// them as plain static methods reduces IL volume and means each
/// emitted predicate is one indirect call away from the implementation.
///
/// <para>Each helper assumes the embedding layer has populated the
/// matching <see cref="Activation"/> field at query setup time. A null
/// field is a programmer error (a non-embedded engine ran IL it
/// shouldn't have) and surfaces as <see cref="InvalidOperationException"/>
/// rather than a silent miscompile.</para>
///
/// <para>Only the PSTR helpers live here — IL non-tail Call dispatch is
/// threaded continuation (no recursive subroutine helper, no meta-CP
/// backtrack-driver), and the PSTR-cell-construction opcodes have no
/// obvious threaded analogue, so the IL emit still routes through
/// them.</para>
/// </summary>
public static class IlRuntimeHelpers
{
    /// <summary>IL <c>get_pstr</c>: build a PSTR cell from the string at
    /// <paramref name="literalId"/> in the engine's current literal pool
    /// and unify it with <c>X[<paramref name="argReg"/>]</c>. Mirrors
    /// what the bytecode interpreter does for the same opcode but
    /// reachable from an IL <c>call</c>.</summary>
    public static bool GetPstr(Activation engine, int literalId, int argReg)
    {
        string s = ResolveStringLiteral(engine, literalId);
        int headerIdx = engine.MakePstr(s, TextKind.Codes);
        return engine.UnifyRegisterWithHeapAt(argReg, headerIdx);
    }

    /// <summary>IL <c>put_pstr</c>: same as <see cref="GetPstr"/> but
    /// writes the resulting REF directly into the register without
    /// unifying (the caller is filling a fresh arg slot).</summary>
    public static void PutPstr(Activation engine, int literalId, int argReg)
    {
        string s = ResolveStringLiteral(engine, literalId);
        int headerIdx = engine.MakePstr(s, TextKind.Codes);
        engine.SetRegister(argReg, Cell.Ref(headerIdx));
    }

    private static string ResolveStringLiteral(Activation engine, int literalId)
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
}

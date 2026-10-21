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
        TextLiteral lit = ResolveStringLiteral(engine, literalId);
        int headerIdx = engine.MakePstr(lit.Text, lit.Kind);
        return engine.UnifyRegisterWithHeapAt(argReg, headerIdx);
    }

    /// <summary>IL <c>put_pstr</c>: same as <see cref="GetPstr"/> but
    /// writes the resulting REF directly into the register without
    /// unifying (the caller is filling a fresh arg slot).</summary>
    public static void PutPstr(Activation engine, int literalId, int argReg)
    {
        TextLiteral lit = ResolveStringLiteral(engine, literalId);
        int headerIdx = engine.MakePstr(lit.Text, lit.Kind);
        engine.SetRegister(argReg, Cell.Ref(headerIdx));
    }

    /// <summary>ADR-063: IL <c>put_blit</c>, the image copied at the heap top
    /// and the register set to the term.</summary>
    public static void PutBlit(Activation engine, int imageId, int argReg)
    {
        var pool = engine.CurrentImages
            ?? throw new InvalidOperationException(
                "IL put_blit: engine.CurrentImages is null. The embedding layer must populate it at query setup.");
        if (imageId < 0 || imageId >= pool.Count)
            throw new InvalidOperationException($"IL put_blit: image id {imageId} is out of range [0, {pool.Count}).");
        engine.SetRegister(argReg, engine.BuildImage(pool[imageId]));
    }

    /// <summary>ADR-063: persisted IL's <c>put_blit</c>, whose id names the
    /// process's image table (a persisted module is loaded once per process
    /// and shared by every engine; the loader's patch interned the image).</summary>
    public static void PutBlitGlobal(Activation engine, int imageId, int argReg)
        => engine.SetRegister(argReg, engine.BuildImage(TermImageTable.Get(imageId)));

    /// <summary>ADR-063, heads: true when the argument was unbound and now
    /// holds the copy; the caller then skips the matching stream.</summary>
    public static bool GetBlit(Activation engine, int imageId, int argReg)
    {
        var pool = engine.CurrentImages
            ?? throw new InvalidOperationException(
                "IL get_blit: engine.CurrentImages is null. The embedding layer must populate it at query setup.");
        if (imageId < 0 || imageId >= pool.Count)
            throw new InvalidOperationException($"IL get_blit: image id {imageId} is out of range [0, {pool.Count}).");
        return engine.GetBlit(pool[imageId], argReg);
    }

    public static bool GetBlitGlobal(Activation engine, int imageId, int argReg)
        => engine.GetBlit(TermImageTable.Get(imageId), argReg);

    /// <summary>ADR-063, heads: whether the register holds the copy mark.</summary>
    public static bool IsCopied(Activation engine, int reg)
        => Activation.IsCopiedMark(engine.GetRegister(reg));

    private static TextLiteral ResolveStringLiteral(Activation engine, int literalId)
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

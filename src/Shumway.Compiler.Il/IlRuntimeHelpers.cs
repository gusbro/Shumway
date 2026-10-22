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
    /// <summary>IL <c>get_pstr</c>: build a PSTR cell from the packed text
    /// <paramref name="literal"/> (a <see cref="TextLiteral"/> the method's
    /// table holds) and unify it with <c>X[<paramref name="argReg"/>]</c>.</summary>
    public static bool GetPstrLiteral(Activation engine, object literal, int argReg)
    {
        var lit = (TextLiteral)literal;
        int headerIdx = engine.MakePstr(lit.Text, lit.Kind);
        return engine.UnifyRegisterWithHeapAt(argReg, headerIdx);
    }

    /// <summary>IL <c>put_pstr</c>: as <see cref="GetPstrLiteral"/>, the REF
    /// written to the register (a fresh argument slot).</summary>
    public static void PutPstrLiteral(Activation engine, object literal, int argReg)
    {
        var lit = (TextLiteral)literal;
        int headerIdx = engine.MakePstr(lit.Text, lit.Kind);
        engine.SetRegister(argReg, Cell.Ref(headerIdx));
    }

    /// <summary>Persisted IL's <c>get_pstr</c>: the packed text at
    /// <paramref name="literalId"/> in the engine's current literal pool, the
    /// id the bundle's module gave it.</summary>
    public static bool GetPstr(Activation engine, int literalId, int argReg)
    {
        TextLiteral lit = ResolveStringLiteral(engine, literalId);
        int headerIdx = engine.MakePstr(lit.Text, lit.Kind);
        return engine.UnifyRegisterWithHeapAt(argReg, headerIdx);
    }

    /// <summary>Persisted IL's <c>put_pstr</c>.</summary>
    public static void PutPstr(Activation engine, int literalId, int argReg)
    {
        TextLiteral lit = ResolveStringLiteral(engine, literalId);
        int headerIdx = engine.MakePstr(lit.Text, lit.Kind);
        engine.SetRegister(argReg, Cell.Ref(headerIdx));
    }

    /// <summary>ADR-063: IL <c>put_blit</c>, the image (a <see cref="TermImage"/>
    /// the method's table holds) copied at the heap top and the register set
    /// to the term.</summary>
    public static void PutBlitLiteral(Activation engine, object image, int argReg)
        => engine.SetRegister(argReg, engine.BuildImage((TermImage)image));

    /// <summary>ADR-063: persisted IL's <c>put_blit</c>, whose id names the
    /// process's image table (a persisted module is loaded once per process
    /// and shared by every engine; the loader's patch interned the image).</summary>
    public static void PutBlitGlobal(Activation engine, int imageId, int argReg)
        => engine.SetRegister(argReg, engine.BuildImage(TermImageTable.Get(imageId)));

    /// <summary>ADR-063, heads: true when the argument was unbound and now
    /// holds the copy; the caller then skips the matching stream.</summary>
    public static bool GetBlitLiteral(Activation engine, object image, int argReg)
        => engine.GetBlit((TermImage)image, argReg);

    public static bool GetBlitGlobal(Activation engine, int imageId, int argReg)
        => engine.GetBlit(TermImageTable.Get(imageId), argReg);

    /// <summary>ADR-063, heads: whether the register holds the copy mark.</summary>
    public static bool IsCopied(Activation engine, int reg)
        => Activation.IsCopiedMark(engine.GetRegister(reg));

    /// <summary>ADR-063, heads: IL <c>unify_blit</c>; false is failure.</summary>
    public static bool UnifyBlitLiteral(Activation engine, object image, int reg)
        => engine.UnifyBlit((TermImage)image, reg);

    public static bool UnifyBlitGlobal(Activation engine, int imageId, int reg)
        => engine.UnifyBlit(TermImageTable.Get(imageId), reg);

    /// <summary>ADR-063, the read-only stream: <see cref="Activation.ReadNested"/>
    /// on the image the method's table holds.</summary>
    public static int ReadNestedLiteral(Activation engine, long cell, int functorId, object image, int offset)
        => engine.ReadNested(cell, functorId, (TermImage)image, offset);

    public static int ReadNestedGlobal(Activation engine, long cell, int functorId, int imageId, int offset)
        => engine.ReadNested(cell, functorId, TermImageTable.Get(imageId), offset);

    public static int ReadNestedListLiteral(Activation engine, long cell, object image, int offset)
        => engine.ReadNestedList(cell, (TermImage)image, offset);

    public static int ReadNestedListGlobal(Activation engine, long cell, int imageId, int offset)
        => engine.ReadNestedList(cell, TermImageTable.Get(imageId), offset);

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

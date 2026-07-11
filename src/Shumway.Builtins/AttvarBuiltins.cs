using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The attributed-variable access predicates (chunk 77, Phase 4). An
/// attributed variable is an unbound variable that additionally carries
/// a set of (module, value) attribute pairs. <c>put_attr/3</c>,
/// <c>get_attr/3</c> and <c>del_attr/2</c> are the surface for
/// attaching, reading and removing those pairs; the <c>attvar/1</c>
/// type test lives in <see cref="TypeBuiltins.IsAttVar"/>.
///
/// <para>The attributes themselves live in the engine's attribute table
/// — keyed by a module's atom id, valued by the heap index of the
/// attribute term. Every mutation is trailed, so attributes attached or
/// removed inside a choice point revert on backtracking. Unifying an
/// attributed variable with a value runs the module's
/// <c>verify_attributes/4</c> hook (chunk 79).</para>
/// </summary>
public static class AttvarBuiltins
{
    /// <summary><c>put_attr(-Var, +Module, +Value)</c> — attaches (or
    /// replaces) <c>Module</c>'s attribute on <c>Var</c>, setting it to
    /// <c>Value</c>. A plain unbound variable is promoted to an
    /// attributed variable in place. Throws <c>type_error(var, _)</c>
    /// when <c>Var</c> is already bound to a non-variable.</summary>
    public static bool PutAttr(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        int valueAddr = RegisterToHeap(engine, 2);
        engine.PutAttr(varAddr, moduleId, valueAddr);
        return true;
    }

    /// <summary><c>get_attr(+Var, +Module, -Value)</c> — unifies
    /// <c>Value</c> with the attribute <c>Module</c> carries on
    /// <c>Var</c>. Fails silently when <c>Var</c> has no such attribute
    /// (or isn't an attributed variable at all).</summary>
    public static bool GetAttr(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        int valueAddr = engine.GetAttr(varAddr, moduleId);
        return valueAddr >= 0 && engine.UnifyRegisterWithHeapAt(2, valueAddr);
    }

    /// <summary><c>del_attr(+Var, +Module)</c> — removes <c>Module</c>'s
    /// attribute from <c>Var</c>. Always succeeds, even when <c>Var</c>
    /// carries no such attribute or isn't an attributed variable.</summary>
    public static bool DelAttr(Activation engine)
    {
        int varAddr = RegisterToHeap(engine, 0);
        int moduleId = ModuleId(engine, 1);
        engine.DelAttr(varAddr, moduleId);
        return true;
    }

    /// <summary>Resolves an argument register to a heap index. A
    /// register holding a REF — the usual shape for a variable argument
    /// — already names a heap cell; an immediate is copied onto a fresh
    /// heap cell. Mirrors the engine's own register materialisation, so
    /// a non-variable passed where a variable is required reaches
    /// <see cref="Activation.PutAttr"/>'s type check intact.</summary>
    private static int RegisterToHeap(Activation engine, int regIdx)
    {
        Cell c = engine.GetRegister(regIdx);
        // REF and ATTVAR both carry a heap home index as payload.
        if (c.Tag is Tag.Ref or Tag.AttVar) return c.AsHeapIndex;
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, c);
        return slot;
    }

    /// <summary>Reads the module argument — required to be a bound atom
    /// — and returns its atom id. Throws <c>instantiation_error</c> for
    /// an unbound module and <c>type_error(atom, _)</c> for a non-atom.</summary>
    private static int ModuleId(Activation engine, int regIdx)
    {
        Cell c = engine.GetRegister(regIdx);
        if (c.Tag == Tag.Ref)
            c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        return c.Tag switch
        {
            Tag.Atom => c.AsAtomId,
            Tag.Ref or Tag.AttVar =>
                throw new PrologRuntimeException("instantiation_error"),
            _ => throw new PrologRuntimeException("type_error", "atom"),
        };
    }
}

using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>The compile environment for the LIVE engine: every encoding the
/// module bakes is the interned resume-marker scheme Tier-1 IL already uses,
/// so wasm-pushed choice points, call continuations and callee dispatch all
/// flow through the interpreter's existing marker path -- backtracking into a
/// wasm CP re-enters the delegate at its retry cursor, a callee's proceed
/// resumes the wasm caller, and an unpromoted callee falls back to its
/// bytecode address, with no interpreter changes.</summary>
public sealed class EngineWasmCompileEnv : IWasmCompileEnv
{
    // Markers are interned pairs, not arithmetic: no cursor-range cap here.
    // Group members carry their own fid at every site, and pcs come in
    // already biased with the linked base, so deopt pcs are the identity.
    private static int Marker(int functorId, int cursor)
        => Activation.EncodeResumeMarker(functorId, cursor);

    public int EncodeBp(int functorId, int cursor) => Marker(functorId, cursor);
    public int EncodeReturnMarker(int functorId, int cursor) => Marker(functorId, cursor);
    public int EncodeCallTarget(int calleeFunctorId) => Marker(calleeFunctorId, 0);
    public int EncodeAddress(int address) => address;

    public bool TryGetBuiltin(int calleeFunctorId, out int builtinId)
        => Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(calleeFunctorId, out builtinId);

    public bool IsDirectBuiltin(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry is { IsCall: false, IsDollarCall: false };
    }

    public bool IsInlineUnify(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "=" && entry.Arity == 2;
    }

    public bool IsInlineCompare(int builtinId, out bool negated)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        negated = entry.Name == "\\==";
        return entry.Arity == 2 && entry.Name is "==" or "\\==";
    }

    public int MqualFunctorId { get; } =
        FunctorTable.Intern(AtomTable.Intern("$mqual", permanent: true).Id, 2);

    public IReadOnlyList<(int FunctorId, int BuiltinId)> MetaCallableBuiltins { get; }
        = BuildMetaCallable();

    private static (int, int)[] BuildMetaCallable()
    {
        var list = new List<(int, int)>();
        foreach (var e in Shumway.Builtins.BuiltinsRegistry.AllEntries())
            list.Add((FunctorTable.Intern(
                AtomTable.Intern(e.Name, permanent: true).Id, e.Arity), e.Id));
        return list.ToArray();
    }

    public bool IsInlineAppend(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "append" && entry.Arity == 3;
    }

    public bool IsInlineBarrierCall(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "$call" && entry.Arity == 2;
    }

    public bool IsInlineMetaCall(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "call" && entry.Arity == 1;
    }

    public bool IsInlineMetaCallN(int builtinId, out int appended)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        appended = entry.Arity - 1;
        // call/1 keeps its own form; 2..8 append 1..7 arguments. The
        // upper bound is the register file the meta-call already
        // sizes, not a property of call/N.
        return entry.Name == "call" && entry.Arity >= 2
            && entry.Arity <= MaxInlineMetaCallArity;
    }

    /// <summary>The widest call/N the inline form takes. Matches the
    /// emitter's MaxMetaCallArity: wider goals go to the host.</summary>
    public const int MaxInlineMetaCallArity = 8;

    public bool IsInlineGetAttr(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "get_attr" && entry.Arity == 3;
    }

    public bool IsInlineDomSame(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "$dom_same" && entry.Arity == 2;
    }

    public bool IsInlineDomEmpty(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "$dom_empty" && entry.Arity == 1;
    }

    public bool IsInlineDomContains(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "$dom_contains" && entry.Arity == 2;
    }

    public bool IsInlineDomDel(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "$dom_del" && entry.Arity == 3;
    }

    public bool IsInlineDomSingleton(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return entry.Name == "$dom_singleton" && entry.Arity == 2;
    }

    public bool TryGetInlineTypeTest(int builtinId, out WasmTypeTest test)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        test = entry.Arity == 1 ? entry.Name switch
        {
            "var" => WasmTypeTest.Var,
            "nonvar" => WasmTypeTest.Nonvar,
            "integer" => WasmTypeTest.Integer,
            "float" => WasmTypeTest.Float,
            "number" => WasmTypeTest.Number,
            "atom" => WasmTypeTest.Atom,
            "atomic" => WasmTypeTest.Atomic,
            "compound" => WasmTypeTest.Compound,
            _ => WasmTypeTest.None,
        } : WasmTypeTest.None;
        return test != WasmTypeTest.None;
    }
}

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
    public int EncodeDeoptPc(int bytecodePc) => bytecodePc;

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
}

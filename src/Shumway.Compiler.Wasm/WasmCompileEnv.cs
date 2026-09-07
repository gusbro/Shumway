using Shumway.Core;

using Shumway.Compiler.Wam;

namespace Shumway.Compiler.Wasm;

/// <summary>A predicate the compiler refuses: an opcode outside the
/// translatable set, or a shape the backend does not do yet. Refusal is the
/// normal outcome for most predicates -- they stay on the tier they were on.
/// </summary>
public sealed class WasmCompileException(string reason) : Exception(reason);

/// <summary>What the emitted code bakes in wherever it has to name something
/// outside itself. The values differ by world -- the engine bakes interned
/// resume markers and linked addresses, the desktop test harness bakes its
/// own small encodings -- and the compiled code does not care, which is what
/// keeps it testable without a browser (the plan's D6: constants for JIT).
/// </summary>
public interface IWasmCompileEnv
{
    /// <summary>The BP field of a choice point pushed by
    /// <paramref name="functorId"/> whose alternatives continue at the
    /// biased bytecode <paramref name="address"/>. ADDRESSES, not cursor
    /// ordinals: a promotion rebuilds the group and renumbers cursors, but
    /// addresses never move, so choice points outlive the build that pushed
    /// them. The compiled fail path compares BP against each of the group's
    /// own encodings to backtrack locally; anything else returns
    /// <see cref="WasmVerdict.Fail"/> for the host to handle.</summary>
    int EncodeBp(int functorId, int address);

    /// <summary>The continuation (CP) a call site of
    /// <paramref name="functorId"/> leaves behind, so that the callee's
    /// proceed re-enters it at the biased bytecode
    /// <paramref name="address"/> (same rebuild-stability rule as
    /// <see cref="EncodeBp"/>). In the engine this is an interned resume
    /// marker; in a group the value is ALSO baked into the proceed jump
    /// table, so an in-group callee returns without leaving the
    /// module.</summary>
    int EncodeReturnMarker(int functorId, int address);

    /// <summary>The Pc value that dispatches <paramref name="calleeFunctorId"/>
    /// (a linked address in the engine; an index in the harness).</summary>
    int EncodeCallTarget(int calleeFunctorId);

    /// <summary>The Pc value for stepping aside at bytecode address
    /// <paramref name="bytecodePc"/> (predicate-local). In the engine this is
    /// the predicate's linked base plus the offset.</summary>
    int EncodeDeoptPc(int bytecodePc);

    /// <summary>Whether a call site's callee is a builtin, and which. The
    /// compiled code then requests it through
    /// <see cref="WasmVerdict.BuiltinRequest"/> instead of dispatching it --
    /// the linker makes the same decision when it rewrites Call to
    /// CallBuiltin.</summary>
    bool TryGetBuiltin(int calleeFunctorId, out int builtinId);

    /// <summary>Whether the builtin can be invoked directly through the
    /// request protocol (entry.Impl against the engine). Meta-call builtins
    /// (call/N, the $call helpers) need the interpreter's dispatch machinery
    /// instead: the compiled code deopts at the call site and the interpreter
    /// re-runs it whole.</summary>
    bool IsDirectBuiltin(int builtinId);

    /// <summary>Whether the builtin is =/2, which the compiled code
    /// open-codes as its own unifier instead of stepping out: measured, a
    /// leaf clause ending in a unification (tak's <c>A = Z</c>) otherwise
    /// pays a host round-trip PER LEAF, and in the browser the host side is
    /// interpreted C#. Attvars and exotic shapes still deopt inside the
    /// unifier, so semantics are the engine's.</summary>
    bool IsInlineUnify(int builtinId);
}

/// <summary>A compiled predicate: the module bytes plus what the installer
/// needs to know about it.</summary>
public sealed record WasmEntry(
    byte[] Module,
    int FunctorId,
    int Arity,
    /// <summary>Bytecode address to cursor id, for every re-entry point the
    /// module has. Cursor 0 is address 0, the fresh entry.</summary>
    System.Collections.Generic.IReadOnlyDictionary<int, int> CursorByAddress,
    /// <summary>X registers the module addresses (highest index + 1). The
    /// host must guarantee the register area covers this before entering --
    /// an out-of-range wasm store corrupts whatever lies beyond.</summary>
    int RegisterDemand);

/// <summary>One predicate of a group compile: its compiled form, the BIAS
/// that offsets its bytecode addresses into the group's unified pc space
/// (the linked base in the engine, so a deopt pc needs no translation), and
/// its float-literal pool.</summary>
public sealed record WasmGroupMember(
    CompiledPredicate Predicate,
    int Bias,
    System.Collections.Generic.IReadOnlyList<double>? FloatLiterals);

/// <summary>A compiled GROUP module: one dispatcher over every member's
/// code, global cursors, cross-member calls as internal jumps.</summary>
public sealed record WasmGroupEntry(
    byte[] Module,
    /// <summary>Each member functor's fresh-entry cursor.</summary>
    System.Collections.Generic.IReadOnlyDictionary<int, int> EntryCursorByFid,
    /// <summary>Biased bytecode address to cursor id, for every re-entry
    /// point the module has.</summary>
    System.Collections.Generic.IReadOnlyDictionary<int, int> CursorByAddress,
    /// <summary>X registers the module addresses (highest index + 1),
    /// across all members.</summary>
    int RegisterDemand);

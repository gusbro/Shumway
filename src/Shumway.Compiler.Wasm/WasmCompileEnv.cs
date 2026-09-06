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
    /// <summary>The BP field of a choice point whose alternatives continue at
    /// <paramref name="cursor"/>. The compiled fail path compares BP against
    /// each of its own encodings to backtrack locally; anything else returns
    /// <see cref="WasmVerdict.Fail"/> for the host to handle.</summary>
    int EncodeBp(int cursor);

    /// <summary>The continuation (CP) a call site leaves behind, so that the
    /// callee's proceed re-enters this predicate at
    /// <paramref name="cursor"/>. In the engine this is an interned resume
    /// marker.</summary>
    int EncodeReturnMarker(int cursor);

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
}

/// <summary>A compiled predicate: the module bytes plus what the installer
/// needs to know about it.</summary>
public sealed record WasmEntry(
    byte[] Module,
    int FunctorId,
    int Arity,
    /// <summary>Bytecode address to cursor id, for every re-entry point the
    /// module has. Cursor 0 is address 0, the fresh entry.</summary>
    System.Collections.Generic.IReadOnlyDictionary<int, int> CursorByAddress);

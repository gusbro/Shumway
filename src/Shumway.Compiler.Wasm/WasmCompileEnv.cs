using Shumway.Core;

using Shumway.Compiler.Wam;

namespace Shumway.Compiler.Wasm;

/// <summary>A predicate the compiler refuses: an opcode outside the
/// translatable set, or a shape the backend does not do yet. Refusal is the
/// normal outcome for most predicates -- they stay on the tier they were on.
/// </summary>
public class WasmCompileException(string reason) : Exception(reason);

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

    /// <summary>A biased bytecode <paramref name="address"/> the module hands
    /// the host as is: the pc of a deopt, the cursor a builtin request
    /// returns to. The identity in the engine (addresses come in already
    /// linked); a relocating env hands back a sentinel instead.</summary>
    int EncodeAddress(int address);

    /// <summary>The BuiltinId mailbox value for a request: the id in the
    /// low half, the env-trim size in the high half (-1 = no trim, 0 for a
    /// tail request).</summary>
    long EncodeBuiltinId(int builtinId, int envTrim)
        => (uint)builtinId | ((long)envTrim << 32);

    /// <summary>The cell an atom immediate bakes as.</summary>
    long AtomCell(int atomId) => Cell.Atom(atomId).Data;

    /// <summary>The cell a functor immediate bakes as.</summary>
    long FunctorCell(int functorId) => Cell.Functor(functorId).Data;

    /// <summary>The module's own id wherever the code compares against it.
    /// </summary>
    int EncodeModuleId(int moduleId) => moduleId;

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

    /// <summary>Whether the builtin is <c>==/2</c> (negated=false) or
    /// <c>\==/2</c> (negated=true) — term identity, whose ATOMIC fast path
    /// the module open-codes: two dereferenced atomic cells are identical
    /// exactly when they are the same cell. Anything non-atomic falls back
    /// to the builtin exit. Measured: crypt's \== chains cost 183k chain
    /// exits per run in the browser, a 31x SLOWDOWN over Tier-0.</summary>
    bool IsInlineCompare(int builtinId, out bool negated)
    {
        negated = false;
        return false;
    }

    /// <summary>Whether the builtin is a one-argument TYPE TEST the module
    /// can answer itself: a tag comparison on the dereferenced argument, no
    /// heap, no binding, no host. Measured on the two libraries that lean on
    /// them hardest, as a share of all builtin exits: 60% of clpr's (var/1
    /// 32%, number/1 22%, nonvar/1 3%, integer/1 3%) and 39% of clpfd's
    /// (integer/1 36%, var/1 3%). They are the cheapest thing in that
    /// ranking and the largest share of it.</summary>
    bool TryGetInlineTypeTest(int builtinId, out WasmTypeTest test)
    {
        test = WasmTypeTest.None;
        return false;
    }

    /// <summary>Whether the builtin is <c>get_attr/3</c>, which the module
    /// answers out of the attribute image in linear memory instead of
    /// stepping out. It is the largest single source of builtin exits there
    /// is: 12,600 in a clpr run, against 6,000 for the next one. Only 1,200
    /// of those fail, which is why the image had to exist at all -- the
    /// failing path alone was not worth open-coding.
    ///
    /// <para>The inline path covers an attributed variable and a bound atom
    /// module. Anything else -- no image staged, a non-attvar, an unbound or
    /// non-atom module, a probe that runs long -- falls back to the exit,
    /// where the builtin's errors and corner cases stay the engine's.</para>
    /// </summary>
    bool IsInlineGetAttr(int builtinId) => false;

    /// <summary>Whether the builtin is <c>call/1</c>, whose goal the module
    /// can dispatch itself through the call-marker table instead of stepping
    /// aside. Every other meta-call shape (call/N with extra arguments,
    /// '$call'/2) still steps aside: they rebuild the goal term first, which
    /// is work the module has no business doing.</summary>
    bool IsInlineMetaCall(int builtinId) => false;

    /// <summary>Whether the builtin is <c>'$call'/2</c>, the meta-call that
    /// CARRIES its cut barrier: X0 is the goal and X1 is the barrier the
    /// enclosing call established, as an integer.
    ///
    /// <para>Same dispatch as call/1 in every other respect, so the module
    /// takes it the same way and only reads the barrier from the argument
    /// instead of from B. Measured in clpr, it is a third of the deopts that
    /// remain: the control helpers the prelude expands disjunctions into
    /// reach their branches through it.</para>
    ///
    /// <para>The body conversion call/1 does (SS7.6.2, wrapping variable
    /// sub-goals) is skipped here by the host, and the module skips it for
    /// call/1 too -- soundly, because only a CONTROL CONSTRUCT can need it
    /// and one never reaches the module's cache: the host rewrites those to
    /// barrier helpers, which are deliberately not published.</para></summary>
    bool IsInlineBarrierCall(int builtinId) => false;

    /// <summary>Whether the builtin is <c>append/3</c>, whose DETERMINISTIC
    /// mode the module builds itself.
    ///
    /// <para>It is the single largest source of builtin exits measured: 2,000
    /// of clpr's 5,000, twice the next one. And the cost is not the work --
    /// measured, getting to a builtin and back costs about four times what
    /// the builtin does.</para>
    ///
    /// <para>Only (+, ?, -): a proper list in the first argument. An unbound
    /// tail is append/3's OTHER mode, which enumerates splits off a choice
    /// point, and a packed string is a list the module cannot walk. Both step
    /// out, as does anything else.</para></summary>
    bool IsInlineAppend(int builtinId) => false;

    /// <summary>The functor id of <c>'$mqual'/2</c>, the wrapper a meta-call
    /// carries so its bare goal functor resolves against the meta-caller's
    /// module first. The module has to see THROUGH it: the goal in X0 is the
    /// wrapper, not the goal.</summary>
    int MqualFunctorId { get; }
}

/// <summary>The one-argument type tests the module open-codes. Each is a
/// set of tags, and the set is the builtin's contract -- notably a VARIABLE
/// is Ref OR AttVar, because an attributed variable has attributes and no
/// value, and the libraries that call var/1 most are the ones that create
/// attributed variables.</summary>
public enum WasmTypeTest
{
    None = 0,
    Var,
    Nonvar,
    Integer,
    Float,
    Number,
    Atom,
    Atomic,
    Compound,
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
    int RegisterDemand,
    /// <summary>(caller functor, callee functor) to the number of call sites
    /// between them, counted while compiling. STATIC: it says which edges
    /// exist and how tightly the code is coupled, NOT how often an edge is
    /// taken at run time.</summary>
    System.Collections.Generic.IReadOnlyDictionary<(int Caller, int Callee), int> CallSites,
    /// <summary>The module id baked into the code; the installing world's
    /// id has to be this one.</summary>
    int ModuleId = 0);

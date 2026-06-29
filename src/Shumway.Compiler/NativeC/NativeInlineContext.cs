using System;
using System.Reflection;

namespace Shumway.Compiler.NativeC;

/// <summary>ADR-022 item 2 — one embedded native block's body, handed to the
/// build-time IL inline emitter by the engine (which owns the block table). The
/// emitter lives in <c>Shumway.Compiler.Il</c>, which can reference
/// <c>Shumway.Compiler.NativeC</c> but not <c>Shumway.Embedding</c>, so the block
/// travels as these Compiler-level types.</summary>
public sealed record NativeBlockBody(NativeVar[] Vars, CStmt[] Stmts, NativeScalarGlobal[] ScalarGlobals);

/// <summary>ADR-022 item 2 — the context the IL compiler needs to inline a
/// <c>'$native_run'('$nb$…', regs)</c> call directly into a predicate's IL,
/// instead of dispatching it as a builtin. It is supplied by
/// <c>Shumway.Embedding</c> (which owns the block table and the interop class) and
/// carries everything the engine-less IL compiler can't reach on its own:
///
/// <list type="bullet">
/// <item>the block lookup (<see cref="BlockProvider"/>) and interop resolver
///   (<see cref="InteropResolver"/>);</item>
/// <item>the reflection handles for the marshalling the emitted IL calls —
///   <c>RegisterMarshalling</c> read/unify, <c>Engine.Host</c>, the typed
///   <c>FromTerm</c>/<c>ToTerm</c> overloads on the host, and the <c>AtomTerm</c>
///   constructor — none of whose declaring types <c>Shumway.Compiler.Il</c> may
///   name at compile time.</item>
/// </list>
///
/// <para>Interop calls are emitted as a direct <c>call</c> to the resolved
/// <see cref="MethodInfo"/> (the method's declaring assembly is referenced
/// cross-assembly; for a persisted-IL bundle the runtime resolves it at load via
/// the foreign-DLL auto-load). When the block, a variable's type, or an interop
/// function can't be resolved, the emitter declines and the call stays a normal
/// builtin dispatch (which runs the block via the interpreter / runtime
/// delegate).</para></summary>
public sealed class NativeInlineContext
{
    public required Func<string, NativeBlockBody?> BlockProvider { get; init; }
    public required Func<string, MethodInfo?> InteropResolver { get; init; }

    public required MethodInfo ReadRegisterAsTerm { get; init; }   // (Engine, int) -> Term
    public required MethodInfo UnifyRegisterWithTerm { get; init; } // (Engine, int, Term) -> bool
    public required MethodInfo HostGetter { get; init; }            // Engine.Host -> object
    public required Type HostType { get; init; }                    // typeof(PrologEngine)
    public required MethodInfo FromTermLong { get; init; }          // host.FromTerm<long>(Term)
    public required MethodInfo FromTermDouble { get; init; }
    public required MethodInfo FromTermString { get; init; }
    public required MethodInfo ToTermLong { get; init; }            // host.ToTerm<long>(long)
    public required MethodInfo ToTermDouble { get; init; }
    public required ConstructorInfo AtomTermCtor { get; init; }     // new AtomTerm(string)

    // ADR-022 — persistent scalar `:- c` global accessors (host = PrologEngine).
    public required MethodInfo GetNativeGlobalInt { get; init; }    // host.GetNativeGlobalInt(string) -> long
    public required MethodInfo SetNativeGlobalInt { get; init; }    // host.SetNativeGlobalInt(string, long)
    public required MethodInfo GetNativeGlobalFloat { get; init; }  // host.GetNativeGlobalFloat(string) -> double
    public required MethodInfo SetNativeGlobalFloat { get; init; }  // host.SetNativeGlobalFloat(string, double)

    // ADR-024 reftype tier — the handles for emitting reftype operations inline
    // (the TermSlot type and the engine/host methods Compiler.Il can't name).
    public Type? TermSlotType { get; init; }                        // typeof(TermSlot)
    public MethodInfo? GetOrCreateReftypeSlot { get; init; }        // host.GetOrCreateReftypeSlot(string) -> TermSlot
    public MethodInfo? MakeForeign { get; init; }                   // engine.MakeForeign(object) -> Cell
    public MethodInfo? UnifyRegisterWithCell { get; init; }         // engine.UnifyRegisterWithCell(int, Cell) -> bool
    public MethodInfo? ReadReftypeSlot { get; init; }               // (Engine, int) -> TermSlot
    public MethodInfo? SlotSetValue { get; init; }                  // slot.SetValue(Term)   [fill_par]
    public MethodInfo? SlotMaterialize { get; init; }               // slot.Materialize() -> Term  [reftype_term]

    /// <summary>True when the reftype handles are present (the host supplied them);
    /// reftype inlining is possible.</summary>
    public bool HasReftype => TermSlotType is not null;

    public MethodInfo FromTermFor(Type model) =>
        model == typeof(string) ? FromTermString
        : model == typeof(double) ? FromTermDouble
        : FromTermLong;

    public MethodInfo ToTermFor(Type model) =>
        model == typeof(double) ? ToTermDouble : ToTermLong;
}

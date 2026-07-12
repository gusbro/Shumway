using System;

namespace Shumway.Core;

/// <summary>
/// The engine-side debug seam (ADR-035). An <see cref="Activation"/> with a
/// non-null <see cref="Activation.Debug"/> reports the Prolog four ports —
/// <c>call</c>, <c>exit</c>, <c>redo</c>, <c>fail</c> — to the session as it
/// runs, and the session decides what to do with them (print a trace line,
/// stop at a breakpoint, complete a step).
///
/// <para>The interface lives in Core so the Tier-0 interpreter can raise the
/// ports without referencing the embedding layer, mirroring
/// <see cref="ITier1Dispatcher"/>. The implementation (which knows how to turn
/// an address into <c>name/arity</c>, render goal arguments and talk to a
/// debugger) lives in <c>Shumway.Embedding</c>.</para>
///
/// <para><b>Cost when disarmed:</b> one null check per goal dispatch, on the
/// branch the CPU predicts not-taken — the same shape as the ESC-cancel flag.
/// Nothing is allocated and no state is tracked unless a session is attached.</para>
///
/// <para><b>Identity of the called goal.</b> Tier-0 dispatch does not carry a
/// single uniform goal identifier: a plain <c>call</c>/<c>execute</c> operand
/// is a code <em>address</em>, the linker-rewritten <c>call_il</c>/<c>execute_il</c>
/// carry a <em>functor id</em>, and <c>call_builtin</c>/<c>execute_builtin</c>
/// carry a <em>builtin id</em>. Rather than force the interpreter to resolve
/// them (which would cost on the hot path), each shape is reported as-is and the
/// session resolves what it needs.</para>
/// </summary>
public interface IDebugSession
{
    /// <summary>Call port for a user predicate dispatched by code address
    /// (plain <c>call</c>/<c>execute</c>, meta-calls, and the linker's
    /// <c>call_bytecode</c>/<c>execute_bytecode</c>).</summary>
    /// <param name="tailCall"><c>true</c> at an <c>execute</c>-family site: the
    /// callee reuses the caller's environment frame, so it takes the caller's
    /// place on the logical stack instead of nesting under it. Reporting this
    /// keeps a port tracer's stack in step with the machine's under last-call
    /// optimisation, without depending on whether LCO is currently enabled.</param>
    void OnCallAddress(Activation engine, int address, bool tailCall);

    /// <summary>Call port for a user predicate dispatched by functor id
    /// (the Tier-1 <c>call_il</c>/<c>execute_il</c> sites).</summary>
    /// <param name="tailCall">See <see cref="OnCallAddress"/>.</param>
    void OnCallFunctor(Activation engine, int functorId, bool tailCall);

    /// <summary>Call port for a builtin (including foreign predicates).</summary>
    /// <param name="tailCall">See <see cref="OnCallAddress"/>: true at an
    /// <c>execute_builtin</c> site, where the builtin returns straight to the
    /// caller's caller.</param>
    void OnCallBuiltin(Activation engine, int builtinId, bool tailCall);

    /// <summary>Exit/fail port of a builtin: builtins run to completion inside
    /// the dispatch, so their result is known immediately.</summary>
    void OnBuiltinResult(Activation engine, int builtinId, bool succeeded);

    /// <summary>Exit port: a bytecode predicate reached <c>proceed</c> and is
    /// about to return to its continuation.</summary>
    void OnExit(Activation engine);

    /// <summary>Redo port: backtracking is about to resume the choice point
    /// currently named by <c>engine.B</c>, whose next alternative starts at
    /// <paramref name="retryPc"/> (<c>-1</c> for an IL choice point — a
    /// backtrackable builtin re-satisfying — which has no bytecode retry
    /// address). Raised before the CP is popped, so <c>engine.B</c> still
    /// identifies it: every goal called after that CP was pushed has just
    /// failed, and the session works out which from it.</summary>
    void OnRedo(Activation engine, int retryPc);

    /// <summary>Fail port: backtracking found no choice point left — the query
    /// itself fails.</summary>
    void OnFail(Activation engine);

    /// <summary>A <see cref="Opcode.Break"/> instruction was reached: control is
    /// about to enter the goal (or clause) at source site
    /// <paramref name="siteId"/> (a <see cref="DebugSiteTable"/> id). Every such
    /// site in debug-compiled code reports here — whether it is a breakpoint the
    /// user armed, or a step should complete on it, is the session's call. Runs
    /// before the goal, so the session sees the arguments as they were passed.
    /// </summary>
    void OnBreak(Activation engine, int siteId);

    /// <summary>ADR-016 mark phase: a session that holds heap indices of its own
    /// (a tracer keeps each open goal's argument cells so it can show what the
    /// goal bound when it exits) must mark them, or the collector will treat
    /// them as garbage.</summary>
    void MarkHeapRoots(Action<int> markCell);

    /// <summary>ADR-016 relocate phase: rewrite every heap index the session
    /// holds through <paramref name="relocIndex"/> (old index → new).</summary>
    void RelocateHeapRoots(Func<int, int> relocIndex);
}

using System;

namespace Shumway.Core;

/// <summary>
/// The engine-side debug seam (ADR-035). An <see cref="Activation"/> with a
/// non-null <see cref="Activation.Debug"/> reports the Prolog four ports â€”
/// <c>call</c>, <c>exit</c>, <c>redo</c>, <c>fail</c> â€” to the session as it
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
/// branch the CPU predicts not-taken â€” the same shape as the ESC-cancel flag.
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
    /// <paramref name="retryPc"/> (<c>-1</c> for an IL choice point â€” a
    /// backtrackable builtin re-satisfying â€” which has no bytecode retry
    /// address). Raised before the CP is popped, so <c>engine.B</c> still
    /// identifies it: every goal called after that CP was pushed has just
    /// failed, and the session works out which from it.</summary>
    void OnRedo(Activation engine, int retryPc);

    /// <summary>Fail port: backtracking found no choice point left â€” the query
    /// itself fails.</summary>
    void OnFail(Activation engine);

    /// <summary>An armed breakpoint was reached at program address
    /// <paramref name="pc"/>: the instruction there â€” a clause entry or the start
    /// of a body goal â€” is about to run, and has not yet. The session armed it, so
    /// it knows which source site the address belongs to. Only ARMED addresses
    /// report here; a debug-compiled program with no breakpoints raises this
    /// never, and costs nothing.</summary>
    void OnBreak(Activation engine, int pc);

    /// <summary>ADR-035 - a goal that compiles INLINE (a <c>!</c>, an <c>is/2</c>, an
    /// <c>=/2</c>, a comparison) is about to run. Those goals emit no call, so no other
    /// port ever fires for them - and a step walked straight over the <c>!</c> the user
    /// wanted to stand at, variables in hand, before it commits. Raised by the
    /// <c>debug_port</c> opcode, which only compile_mode=debug code contains, one byte
    /// before each inline body goal.</summary>
    // The empty body is what lets a session ignore the ports it does not care
    // about — DebugTracer takes neither of these. .NET Framework's runtime has
    // no default interface implementations, so there it is declared and the
    // implementers supply the empty body themselves.
#if NETFRAMEWORK
    void OnInlineGoal(Activation engine);
#else
    void OnInlineGoal(Activation engine) { }
#endif
    /// <summary>Control is leaving Prolog and going back to whoever asked for a
    /// solution â€” the query has produced one, or run out of them. There is no port
    /// here and nothing to show: the machine is not in the program any more.
    ///
    /// <para>It matters because a STEP is a promise to stop at the next port that
    /// satisfies it, and past this line no port is coming. A step nobody can satisfy
    /// has to be abandoned, and said to be abandoned â€” a debugger left waiting for a
    /// stop that will never arrive believes the program is still running, and every
    /// key the user presses after that is answered with an error.</para></summary>
#if NETFRAMEWORK
    void OnLeaveProlog(Activation engine);
#else
    void OnLeaveProlog(Activation engine) { }
#endif

    /// <summary>ADR-016 mark phase: a session that holds heap indices of its own
    /// (a tracer keeps each open goal's argument cells so it can show what the
    /// goal bound when it exits) must mark them, or the collector will treat
    /// them as garbage.</summary>
    void MarkHeapRoots(Action<int> markCell);

    /// <summary>ADR-016 relocate phase: rewrite every heap index the session
    /// holds through <paramref name="relocIndex"/> (old index → new).
    /// <paramref name="engine"/> is the activation whose heap was compacted —
    /// session state indexed on OTHER activations' heaps must not be touched.
    /// <paramref name="relocBoundary"/> maps a saved heap-TOP (an allocation
    /// point, range [0, oldTop] inclusive) — what a debugger's rewind marks
    /// record (ADR-035 D5+): the collection RELOCATES them rather than
    /// invalidating them, so Set Next Statement's backward targets survive a
    /// GC mid-step. Sound because the slide is order-preserving, trailed
    /// cells are roots (no trail entry ever points at a collected cell), and
    /// the trails themselves are relocated in place, never compacted — a
    /// mark's trail tops stay true as they are.</summary>
    void RelocateHeapRoots(
        Activation engine, Func<int, int> relocIndex, Func<int, int> relocBoundary);
}

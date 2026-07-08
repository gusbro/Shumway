using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The trivial control predicates: <c>fail/0</c> always reports failure
/// (triggering backtrack-or-fail), <c>true/0</c> always succeeds.
///
/// <para><c>true/0</c> is rarely emitted as a runtime call because the
/// compiler's <c>FlattenConjunction</c> drops <c>true</c> goals during AST
/// rewriting. It's registered anyway so a meta-level dispatch (if it ever
/// reaches a literal <c>true</c>) does the right thing.</para>
///
/// <para><c>fail/0</c> is essential for the compile-time expansion of
/// negation-as-failure: <c>\+ G</c> rewrites to a helper whose body ends in
/// <c>!, fail</c>.</para>
/// </summary>
public static class ControlBuiltins
{
    public static bool Fail(Engine engine) => false;
    public static bool True(Engine engine) => true;

    /// <summary><c>get_cpu_time(-Time)</c> — GNU-Prolog timing primitive:
    /// binds <c>Time</c> to a high-resolution monotonic process timer, in
    /// milliseconds (a float, so sub-millisecond deltas survive). Used by the
    /// classic benchmark harness (common.pl) the Aquarius/Van Roy programs
    /// share, and by the Logtalk <c>benchmarks</c> object and lgtunit via
    /// <c>os::cpu_time/1</c>.
    ///
    /// <para>It intentionally reads a <see cref="System.Diagnostics.Stopwatch"/>
    /// (QueryPerformanceCounter) rather than
    /// <c>Process.TotalProcessorTime</c>: the engine runs a query on a single
    /// thread, so elapsed monotonic time is an accurate measure of the
    /// computation's cost, and — crucially for timing harnesses that subtract
    /// an empty-loop baseline and divide by the iteration count — it has
    /// sub-microsecond resolution. <c>TotalProcessorTime</c> is only updated
    /// on the ~15.6 ms Windows scheduler tick, which made per-goal benchmark
    /// numbers swing several-fold with the iteration count (they were
    /// dominated by quantisation noise, not by the engine).</para></summary>
    public static bool GetCpuTime(Engine engine)
    {
        double ms = System.Diagnostics.Stopwatch.GetTimestamp()
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return engine.UnifyRegisterWithHeapAt(0, engine.MakeFloat(ms));
    }

    /// <summary><c>halt/0</c> — terminates execution with exit code 0.
    /// Implemented by throwing <see cref="PrologHaltException"/>, which
    /// the outer <c>Query</c> path intercepts and converts into a
    /// clean termination of the iteration.</summary>
    public static bool Halt0(Engine engine) =>
        throw new PrologHaltException(0);

    /// <summary><c>halt(Code)</c> — terminates execution with the given
    /// integer exit code.</summary>
    public static bool Halt1(Engine engine)
    {
        Cell c = engine.GetRegister(0);
        if (c.Tag == Tag.Ref)
        {
            int addr = engine.Deref(c.AsHeapIndex);
            c = engine.GetHeap(addr);
        }
        if (c.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer");
        long code = c.AsInt;
        if (code < int.MinValue || code > int.MaxValue)
            throw new PrologRuntimeException("domain_error", "int32");
        throw new PrologHaltException((int)code);
    }
}

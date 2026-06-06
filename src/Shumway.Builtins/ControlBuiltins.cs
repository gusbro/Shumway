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
    /// binds <c>Time</c> to the CPU time consumed by the process so far, in
    /// milliseconds. Used by the classic benchmark harness (common.pl) that the
    /// Aquarius/Van Roy programs share. We report the .NET process'
    /// total processor time, the closest equivalent.</summary>
    public static bool GetCpuTime(Engine engine) =>
        engine.UnifyRegisterWithCell(0, Cell.Int(
            (long)System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds));

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

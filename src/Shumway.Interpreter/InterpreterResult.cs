namespace Shumway.Interpreter;

/// <summary>
/// Outcome of running a bytecode region through <see cref="BytecodeInterpreter"/>.
/// </summary>
public enum InterpreterResult
{
    /// <summary>The interpreter reached a <c>halt</c> opcode or returned past the top
    /// of the call stack via a <c>proceed</c> with no caller. Either way, execution
    /// finished cleanly with the engine state intact.</summary>
    Halted,

    /// <summary>Execution failed (e.g., a unification mismatched) and no choice point
    /// existed to backtrack to. Reserved for future chunks once unify opcodes land —
    /// nothing in the 5a opcode subset can produce this.</summary>
    Failed,
}

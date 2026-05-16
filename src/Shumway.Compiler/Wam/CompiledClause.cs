namespace Shumway.Compiler.Wam;

/// <summary>
/// The bytecode and metadata produced by compiling a single Prolog clause. The
/// bytecode is self-contained for its head-matching opcodes, but inter-clause
/// references — body goals' <c>call</c> and <c>execute</c> instructions — are
/// emitted with placeholder targets (zero) and listed in <see cref="CallSites"/>.
/// A linker pass (currently the test harness) patches each call site's target
/// operand to the actual address of the callee predicate before the program is
/// handed to the interpreter.
/// </summary>
public sealed class CompiledClause
{
    public byte[] Bytecode { get; }

    /// <summary>The functor id (interned in <see cref="Shumway.Core.FunctorTable"/>)
    /// identifying the head's name and arity.</summary>
    public int FunctorId { get; }

    public int Arity { get; }

    /// <summary>The number of X registers the clause uses, including the argument
    /// registers. Useful when sizing or auditing the engine's register array.</summary>
    public int RegisterCount { get; }

    /// <summary>The number of permanent (Y) slots — equal to the operand of the
    /// clause's <c>allocate</c> instruction (zero if no environment frame is
    /// needed).</summary>
    public int PermanentCount { get; }

    /// <summary>Every <c>call</c> and <c>execute</c> opcode emitted into
    /// <see cref="Bytecode"/> appears here. The linker patches the target
    /// operand (the four bytes starting at
    /// <see cref="CallSite.OpcodeOffset"/> + 1) to the callee predicate's
    /// address.</summary>
    public IReadOnlyList<CallSite> CallSites { get; }

    public CompiledClause(
        byte[] bytecode,
        int functorId,
        int arity,
        int registerCount,
        int permanentCount,
        IReadOnlyList<CallSite> callSites)
    {
        Bytecode = bytecode;
        FunctorId = functorId;
        Arity = arity;
        RegisterCount = registerCount;
        PermanentCount = permanentCount;
        CallSites = callSites;
    }
}

/// <summary>
/// A reference from a clause's bytecode to another predicate. The opcode at
/// <see cref="OpcodeOffset"/> is either <c>call</c> or <c>execute</c> (per
/// <see cref="IsExecute"/>) and its target operand needs to be patched to the
/// callee predicate's bytecode address.
/// </summary>
public readonly record struct CallSite(int OpcodeOffset, int CalleeFunctorId, bool IsExecute);

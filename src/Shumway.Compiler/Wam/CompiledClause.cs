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

    /// <summary>ADR-025 — CLAUSE-LOCAL address operands emitted by the inline
    /// if-then-else lowering (the <c>try_me_else</c> else-target and the
    /// <c>jump</c> end-target). Each entry is the byte offset of a 4-byte
    /// operand whose VALUE is a clause-local address; PredicateCompiler shifts
    /// both the site and the value by the clause's placement offset and folds
    /// them into the predicate's dispatch sites, which the linker then makes
    /// program-absolute. Empty for clauses without inline control flow.</summary>
    public IReadOnlyList<int> DispatchSites { get; }

    /// <summary>ADR-035 — the places a debugger may stop inside this clause: its
    /// entry, and the first instruction of each body goal. Recorded under
    /// <c>compile_mode=debug</c> only, and EMPTY OF BYTECODE — a stop site is a
    /// note about an offset, not an instruction. Arming a breakpoint patches the
    /// opcode byte at that offset to <c>Break</c> and remembers what was there;
    /// nothing is emitted, so debug code that nobody is stopping in runs at full
    /// speed. Offsets are clause-local and are shifted into predicate-local ones
    /// exactly like <see cref="CallSites"/>.</summary>
    public IReadOnlyList<DebugStop> DebugStops { get; }

    public CompiledClause(
        byte[] bytecode,
        int functorId,
        int arity,
        int registerCount,
        int permanentCount,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<int>? dispatchSites = null,
        IReadOnlyList<DebugStop>? debugStops = null)
    {
        Bytecode = bytecode;
        FunctorId = functorId;
        Arity = arity;
        RegisterCount = registerCount;
        PermanentCount = permanentCount;
        CallSites = callSites;
        DispatchSites = dispatchSites ?? Array.Empty<int>();
        DebugStops = debugStops ?? Array.Empty<DebugStop>();
    }
}

/// <summary>ADR-035 — a place a debugger may stop: the bytecode offset of the
/// instruction it precedes, and the id of the source location
/// (<see cref="Shumway.Core.DebugSiteTable"/>) it corresponds to.</summary>
public readonly record struct DebugStop(int Offset, int SiteId);

/// <summary>
/// A reference from a clause's bytecode to another predicate. The opcode at
/// <see cref="OpcodeOffset"/> is either <c>call</c> or <c>execute</c> (per
/// <see cref="IsExecute"/>) and its target operand needs to be patched to the
/// callee predicate's bytecode address.
/// </summary>
public readonly record struct CallSite(int OpcodeOffset, int CalleeFunctorId, bool IsExecute);

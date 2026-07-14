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

    /// <summary>ADR-035 — the clause's source variables and the Y slots they live in,
    /// which is what lets a debugger show them. Recorded under
    /// <c>compile_mode=debug</c> only, where every named variable is made permanent
    /// precisely so that this map can exist: a variable left in an X register is gone
    /// the moment the next call overwrites it.</summary>
    public IReadOnlyList<DebugVariable> DebugVariables { get; }

    /// <summary>Whether the clause allocates an environment frame. A debugger needs to
    /// know: the frames on the environment chain are exactly the clauses that have one,
    /// so this is what aligns a stack frame with the environment its variables live in.
    /// A clause with no frame has no variables to show — there is nowhere to put
    /// them.</summary>
    public bool HasFrame { get; }

    /// <summary>ADR-035 — the head's argument terms, as written. What lets a debugger show
    /// a stack frame as the CALL it is — <c>total([item(book,25)], 10, _G5)</c> — rather
    /// than a bare <c>total/3</c>: each argument is the head skeleton with the clause's
    /// variables substituted by their CURRENT values, so the display instantiates as the
    /// clause runs. Recorded under <c>compile_mode=debug</c> only; null otherwise.</summary>
    public IReadOnlyList<Shumway.Compiler.Ast.Term>? DebugHeadArgs { get; }

    public CompiledClause(
        byte[] bytecode,
        int functorId,
        int arity,
        int registerCount,
        int permanentCount,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<int>? dispatchSites = null,
        IReadOnlyList<DebugStop>? debugStops = null,
        IReadOnlyList<DebugVariable>? debugVariables = null,
        bool hasFrame = false,
        IReadOnlyList<Shumway.Compiler.Ast.Term>? debugHeadArgs = null)
    {
        HasFrame = hasFrame;
        Bytecode = bytecode;
        FunctorId = functorId;
        Arity = arity;
        RegisterCount = registerCount;
        PermanentCount = permanentCount;
        CallSites = callSites;
        DispatchSites = dispatchSites ?? Array.Empty<int>();
        DebugStops = debugStops ?? Array.Empty<DebugStop>();
        DebugVariables = debugVariables ?? Array.Empty<DebugVariable>();
        DebugHeadArgs = debugHeadArgs;
    }
}

/// <summary>ADR-035 — a source variable of a clause, and the Y slot holding it.</summary>
public readonly record struct DebugVariable(string Name, int Slot);

/// <summary>ADR-035 — one clause of a compiled predicate, as a debugger sees it: the
/// half-open span of bytecode it occupies, whether it has an environment frame, and
/// the source variables in that frame.</summary>
public readonly record struct DebugClauseFrame(
    int Start, int End, bool HasFrame, IReadOnlyList<DebugVariable> Variables)
{
    /// <summary>ADR-035 — the head's argument terms (see
    /// <see cref="CompiledClause.DebugHeadArgs"/>); null unless compiled debuggable.</summary>
    public IReadOnlyList<Shumway.Compiler.Ast.Term>? HeadArgs { get; init; }

    /// <summary>ADR-035 — which clause of its predicate this is, 1-based, in source order.
    /// The <c>!2</c> of the debugger's <c>total(...)!2</c>. Zero when unknown.</summary>
    public int ClauseNumber { get; init; }
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

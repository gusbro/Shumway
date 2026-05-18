using Shumway.Compiler.Lexer;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// The compiled bytecode for one predicate — all clauses with the same functor
/// and arity, wrapped (if there are two or more) by the WAM choice-point
/// dispatch instructions <c>try_me_else</c>, <c>retry_me_else</c>, and
/// <c>trust_me</c>. A single-clause predicate has no wrapping; its bytecode is
/// exactly its sole <see cref="CompiledClause"/>'s bytes.
///
/// <para><see cref="CallSites"/> contains every <c>call</c> / <c>execute</c>
/// instruction from any of the predicate's clauses, with offsets translated
/// from clause-local to predicate-local. A <see cref="Linker"/> then shifts
/// those once more when concatenating predicates into a program.</para>
/// </summary>
public sealed class CompiledPredicate
{
    public byte[] Bytecode { get; }
    public int FunctorId { get; }
    public int Arity { get; }
    public int ClauseCount { get; }
    public IReadOnlyList<CallSite> CallSites { get; }

    /// <summary>Byte offsets (inside <see cref="Bytecode"/>) of the four-byte BP
    /// operands embedded in <c>try_me_else</c>, <c>retry_me_else</c>,
    /// <c>try</c>, <c>retry</c>, <c>trust</c> and the four address operands of
    /// <c>switch_on_term</c>. Each site currently holds a predicate-local
    /// address; the linker shifts them by the predicate's base position so the
    /// runtime sees absolute program addresses.</summary>
    public IReadOnlyList<int> DispatchSites { get; }

    /// <summary>Switch tables introduced by this predicate's
    /// <c>switch_on_atom</c> / <c>switch_on_integer</c> /
    /// <c>switch_on_structure</c> instructions. The bytecode references each
    /// by its index in this list (predicate-local); the module-level linker
    /// rewrites the operands to module-level ids.</summary>
    public IReadOnlyList<SwitchTable> SwitchTables { get; }

    /// <summary>Byte offsets (inside <see cref="Bytecode"/>) of the four-byte
    /// table-id operand embedded in <c>switch_on_atom</c> /
    /// <c>switch_on_integer</c> / <c>switch_on_structure</c>. The linker adds
    /// the predicate's switch-table base offset to each.</summary>
    public IReadOnlyList<int> SwitchTableIdSites { get; }

    /// <summary>The source position of the predicate's first clause, or
    /// <see cref="SourcePosition.Start"/> when no position is available
    /// (e.g. synthetic predicates produced by the engine, or predicates
    /// reconstructed from a bundle blob). Used by the runtime stack
    /// trace path (chunk 53) to point users back to the right source
    /// location.</summary>
    public SourcePosition SourcePosition { get; }

    /// <summary>One <see cref="SourcePosition"/> per clause, in source
    /// order. Aligned with the <c>Meta(DbgInfo, clauseIndex)</c> opcodes
    /// the predicate compiler emits at each clause boundary (chunk 55):
    /// the entry id encoded in the Meta payload is the clause's index
    /// into this list. The stack-trace path scans backward from the
    /// error PC for the most recent Meta opcode and reads its
    /// payload to look up the precise clause position. Empty for
    /// predicates with no usable position data (e.g. ones rebuilt from
    /// a bundle blob produced before chunk 55).</summary>
    public IReadOnlyList<SourcePosition> ClauseSourcePositions { get; }

    public CompiledPredicate(
        byte[] bytecode,
        int functorId,
        int arity,
        int clauseCount,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<int> dispatchSites)
        : this(bytecode, functorId, arity, clauseCount, callSites, dispatchSites,
               Array.Empty<SwitchTable>(), Array.Empty<int>())
    {
    }

    public CompiledPredicate(
        byte[] bytecode,
        int functorId,
        int arity,
        int clauseCount,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<int> dispatchSites,
        IReadOnlyList<SwitchTable> switchTables,
        IReadOnlyList<int> switchTableIdSites)
        : this(bytecode, functorId, arity, clauseCount, callSites, dispatchSites,
               switchTables, switchTableIdSites, SourcePosition.Start)
    {
    }

    public CompiledPredicate(
        byte[] bytecode,
        int functorId,
        int arity,
        int clauseCount,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<int> dispatchSites,
        IReadOnlyList<SwitchTable> switchTables,
        IReadOnlyList<int> switchTableIdSites,
        SourcePosition sourcePosition)
        : this(bytecode, functorId, arity, clauseCount, callSites, dispatchSites,
               switchTables, switchTableIdSites, sourcePosition,
               Array.Empty<SourcePosition>())
    {
    }

    public CompiledPredicate(
        byte[] bytecode,
        int functorId,
        int arity,
        int clauseCount,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<int> dispatchSites,
        IReadOnlyList<SwitchTable> switchTables,
        IReadOnlyList<int> switchTableIdSites,
        SourcePosition sourcePosition,
        IReadOnlyList<SourcePosition> clauseSourcePositions)
    {
        Bytecode = bytecode;
        FunctorId = functorId;
        Arity = arity;
        ClauseCount = clauseCount;
        CallSites = callSites;
        DispatchSites = dispatchSites;
        SwitchTables = switchTables;
        SwitchTableIdSites = switchTableIdSites;
        SourcePosition = sourcePosition;
        ClauseSourcePositions = clauseSourcePositions;
    }
}

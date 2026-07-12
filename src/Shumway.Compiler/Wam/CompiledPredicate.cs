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

    private byte[]? _bytecodeUnfused;

    /// <summary>ADR-029 — <see cref="Bytecode"/> with every fused clause-epilogue
    /// opcode (<c>cut_deallocate_proceed</c> / <c>cut_proceed</c>) expanded back
    /// to its two components (<c>cut</c> + <c>deallocate_proceed</c> / <c>cut</c>
    /// + <c>proceed</c>). The fused forms are Nop-padded to the summed width, so
    /// this array is the SAME length and every recorded offset (CallSites,
    /// DispatchSites, clause ranges) stays valid against it. The Tier-1 IL
    /// describers / emitters read THIS, so they never encounter a fused opcode
    /// and need no per-opcode handling; the Tier-0 interpreter runs the fused
    /// <see cref="Bytecode"/>. Lazily computed; returns <see cref="Bytecode"/>
    /// itself when nothing is fused (fusion disabled, or a predicate with no
    /// cut-terminated clause). Benign init race — the result is content-identical
    /// and the reference write is atomic.</summary>
    public byte[] BytecodeUnfused
    {
        get
        {
            if (_bytecodeUnfused is not null) return _bytecodeUnfused;
            byte[]? copy = null;
            int pc = 0;
            while (pc < Bytecode.Length)
            {
                byte b = Bytecode[pc];
                int size = OpcodeTable.Get(b).Size;
                if (size == 0) break;
                var op = (Opcode)b;
                if (op is Opcode.CutDeallocateProceed or Opcode.CutProceed)
                {
                    copy ??= (byte[])Bytecode.Clone();
                    copy[pc] = (byte)Opcode.Cut;                 // operand (slot) at +1 unchanged
                    copy[pc + 5] = (byte)(op == Opcode.CutDeallocateProceed
                        ? Opcode.DeallocateProceed : Opcode.Proceed);
                }
                pc += size;
            }
            return _bytecodeUnfused = copy ?? Bytecode;
        }
    }
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

    /// <summary>Chunk 430 — memoized result of
    /// <see cref="ModuleCompiler.IsCachedPredicateReusable"/>. The bytecode
    /// is immutable once constructed, so the literal-pool scan is a pure
    /// function of this instance; query setup used to re-walk the whole
    /// predicate's bytecode per cached predicate per query. Encoding:
    /// 0 = not yet computed, 1 = false, 2 = true. An <c>int</c> (not
    /// <c>bool?</c>) so the lazy write is atomic — compiled predicates are
    /// shared across engines via the global caches.</summary>
    internal int PoolFreeMemo;

    /// <summary>Chunk 433 — memoized Tier-1 IL shape analyses (the
    /// <see cref="PoolFreeMemo"/> precedent). Each slot holds the IL
    /// compiler's <c>IlShapeMemo</c> for one shape recogniser
    /// (chunk-216 indexed dispatch, try_me_else chain, switched chain,
    /// indexed-atom): the structural describe result — a pure function of
    /// the immutable bytecode / call sites / switch tables — plus the
    /// recorded <c>Call</c>-site callee fids whose calleeMap resolvability
    /// is re-checked per call. <c>object</c>-typed because the result types
    /// live in <c>Shumway.Compiler.Il</c> (which sees these internals via
    /// InternalsVisibleTo). A reference write is atomic, so the lazy
    /// once-write is safe for predicates shared across engines (a benign
    /// race recomputes the same value).</summary>
    internal object? IlIndexedShapeMemo;
    /// <summary>See <see cref="IlIndexedShapeMemo"/> (chunk 433).</summary>
    internal object? IlTryMeElseShapeMemo;
    /// <summary>See <see cref="IlIndexedShapeMemo"/> (chunk 433).</summary>
    internal object? IlSwitchedChainShapeMemo;
    /// <summary>See <see cref="IlIndexedShapeMemo"/> (chunk 433).</summary>
    internal object? IlIndexedAtomShapeMemo;

    /// <summary>ADR-034 — this predicate is a STATIC-style SNAPSHOT of a
    /// dynamic predicate's clauses (ADR-023 <c>BuildDynamicSnapshot</c>): no
    /// <c>enter_dynamic</c>/<c>check_visible</c>, so it is structurally
    /// indistinguishable from a static predicate — but its truth can change
    /// at runtime (assert/retract). Callers must NOT inline it except through
    /// the ADR-034 checked-guard machinery (a clause-entry staleness test +
    /// un-inlined fallback); the predicate's OWN delegate is evictable
    /// (<c>IlPromotionStore.EvictDelegate</c>) so by-fid dispatch stays
    /// live.</summary>
    public bool IsDynamicSnapshot { get; set; }

    /// <summary>ADR-034 — the snapshot's clause set contains RULE clauses
    /// (bodies). The practical Arity model: a dynamic that ships rules
    /// (<c>:- visible</c> for findall/setof meta-call visibility) is
    /// mutation-cold and eligible for checked caller-inlining; a fact-only
    /// dynamic is a real assert/retract target and is never
    /// caller-inlined.</summary>
    public bool SnapshotRuleBearing { get; set; }

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

    /// <summary>ADR-035 — the places a debugger may stop inside this predicate,
    /// with offsets already shifted from clause-local to predicate-local (the
    /// linker shifts them once more, by the predicate's base, when it lays the
    /// program out). Empty unless the predicate was compiled debuggable.
    /// A settable property rather than a constructor parameter because the four
    /// assembly paths already thread nine of those.</summary>
    public IReadOnlyList<DebugStop> DebugStops { get; set; } = Array.Empty<DebugStop>();

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

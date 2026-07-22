using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles all clauses of a single predicate into one <see cref="CompiledPredicate"/>.
/// The clauses must share a functor name and arity; callers (typically
/// <see cref="ModuleCompiler"/>) are responsible for grouping a source file's
/// flat clause stream by functor before invoking this.
///
/// <para><b>Single-clause case.</b> If there's exactly one clause the predicate's
/// bytecode is just that clause's bytes — no dispatch wrapping is needed because
/// no alternative exists to backtrack to.</para>
///
/// <para><b>Multi-clause case, no indexing opportunity.</b> If every clause has
/// a variable first argument (or the predicate is 0-ary), the compiler emits the
/// classical <c>try_me_else</c> / <c>retry_me_else</c> / <c>trust_me</c> chain
/// where each clause body is inlined right after its dispatch instruction. The
/// embedded BPs are pre-computed in pass 1 so pass 2 emits without
/// back-patching.</para>
///
/// <para><b>Multi-clause case, first-argument indexing.</b> When at least one
/// clause discriminates by the type/value of <c>A1</c>, the compiler emits
/// (per ADR-007):
/// <code>
///   switch_on_term VarLbl, ConstLbl, ListLbl, StructLbl
///   VarLbl:    try / retry / trust over every clause in source order
///   ConstLbl:  switch_on_atom (if atom clauses) → switch_on_integer (if int
///              clauses) → fall through to VarLbl as the last resort
///   ListLbl:   try / retry / trust over (list clauses + var clauses)
///   StructLbl: switch_on_structure
///   &lt;per-group chains for atom / int / struct groups with 2+ clauses&gt;
///   &lt;clause bodies, one after another, no dispatch wrapper&gt;
/// </code>
/// Single-clause groups skip the chain — the switch table maps the key
/// straight to the clause body. Variable-headed clauses join every bucket so
/// they're tried in source order alongside the type-specific clauses.</para>
/// </summary>
public sealed class PredicateCompiler
{
    /// <summary>when <c>false</c>, the compiler omits the
    /// <see cref="Opcode.Meta"/>+<see cref="MetaSubOpcode.DbgInfo"/>
    /// per-clause source-position markers from the emitted bytecode.
    /// Release mode in <c>shumway-compile</c> sets this so a stripped
    /// .shmo carries no debug bytes — both the source-string field
    /// AND the in-bytecode debug markers are gone for IP-protection
    /// builds. Default <c>true</c> keeps backward compatibility with
    /// every existing caller (PrologEngine consult, tests).</summary>
    public bool EmitDebugInfo { get; set; } = true;

    /// <summary>ADR-035 — debug codegen: every rule clause keeps a frame, and its
    /// last call becomes <see cref="Opcode.DebugLastCall"/> so last-call
    /// optimisation is a runtime switch. Set by the engine's consult path from
    /// <c>compile_mode=debug</c>. Deliberately a separate option from
    /// <see cref="EmitDebugInfo"/>, which defaults to <c>true</c> here and would
    /// otherwise change codegen for every existing caller.</summary>
    public bool DebugCodegen { get; set; }

    /// <summary>ADR-035 — the <see cref="DebugSiteTable"/> file id these clauses
    /// came from; stamped into every stop site emitted under
    /// <see cref="DebugCodegen"/>.</summary>
    public int DebugFileId { get; set; }

    /// <summary>ADR-029 — clause-epilogue peephole fusion. When set, the final
    /// bytecode of every compiled predicate has each `cut; deallocate_proceed`
    /// and `cut; proceed` pair collapsed into one dispatched opcode (same total
    /// width, Nop-padded — no offset shifts). Tier-0 dispatch-count win; the IL
    /// describer un-fuses the opcodes, so promotion is unaffected. (Deallocate
    /// Execute is deferred: `execute` is a link-time dispatch site the engine
    /// rewrites to ExecuteIl/ExecuteBuiltin, so fusing it would hide that swap.)</summary>
    public static bool EnableEpilogueFusion { get; set; } = true;

    public CompiledPredicate Compile(IReadOnlyList<Clause> clauses)
        => Compile(clauses,
            new LiteralPool<string>(),
            new LiteralPool<double>(),
            new LiteralPool<System.Numerics.BigInteger>());

    /// <summary>Public entry — compiles then applies the ADR-029 epilogue
    /// fusion to the final bytecode (in place; the fused opcodes keep the same
    /// total width so all recorded offsets stay valid).</summary>
    public CompiledPredicate Compile(
        IReadOnlyList<Clause> clauses,
        LiteralPool<string> stringLiterals,
        LiteralPool<double> floatLiterals,
        LiteralPool<System.Numerics.BigInteger> bigIntLiterals,
        bool enableIndexing = true,
        bool isDynamic = false,
        int failStubAddr = 0)
    {
        CompiledPredicate result = CompileCore(clauses, stringLiterals, floatLiterals,
            bigIntLiterals, enableIndexing, isDynamic, failStubAddr);
        if (EnableEpilogueFusion) FuseEpilogues(result.Bytecode);
        return result;
    }

    /// <summary>Collapse each `cut; deallocate_proceed` and `cut; proceed`
    /// adjacency into its fused opcode, in place. Opcode-aligned walk (never
    /// mis-reads an operand byte as a cut); the fused opcode reuses the cut's
    /// operand at +1 and Nop-fills the terminator's opcode byte, so the total
    /// width and every following offset are unchanged. The fused pairs carry no
    /// link-time dispatch operand, so this is safe pre-link.</summary>
    internal static void FuseEpilogues(byte[] code)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            byte b = code[pc];
            int size = OpcodeTable.Get(b).Size;
            if (size == 0) break;   // corruption — stop
            int next = pc + size;
            if ((Opcode)b == Opcode.Cut && next < code.Length)
            {
                switch ((Opcode)code[next])
                {
                    case Opcode.DeallocateProceed:
                        code[pc] = (byte)Opcode.CutDeallocateProceed;
                        code[next] = (byte)Opcode.Nop;   // 2nd byte already Nop
                        break;
                    case Opcode.Proceed:
                        code[pc] = (byte)Opcode.CutProceed;
                        code[next] = (byte)Opcode.Nop;
                        break;
                }
            }
            pc = next;
        }
    }

    /// <summary><paramref name="enableIndexing"/> (JIT
    /// indexing) gates first-arg / multi-arg indexing. When false the
    /// predicate always compiles to the plain <c>try_me_else</c> chain,
    /// even if its clauses would otherwise discriminate. The engine
    /// passes <c>false</c> for dynamic predicates that haven't proven
    /// hot yet: building switch tables is wasted work for a predicate
    /// that's rarely called or constantly churning. Once the runtime
    /// call count crosses the JIT threshold the engine recompiles with
    /// <c>enableIndexing: true</c>.</summary>
    private CompiledPredicate CompileCore(
        IReadOnlyList<Clause> clauses,
        LiteralPool<string> stringLiterals,
        LiteralPool<double> floatLiterals,
        LiteralPool<System.Numerics.BigInteger> bigIntLiterals,
        bool enableIndexing = true,
        bool isDynamic = false,
        int failStubAddr = 0)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        if (clauses.Count == 0)
            throw new ArgumentException("At least one clause is required.", nameof(clauses));

        // Compile each clause independently.
        var compiledClauses = new List<CompiledClause>(clauses.Count);
        var compiler = new ClauseCompiler
            { DebugCodegen = DebugCodegen, DebugFileId = DebugFileId };
        foreach (var c in clauses)
            compiledClauses.Add(compiler.Compile(c, stringLiterals, floatLiterals, bigIntLiterals));

        // Verify all clauses share the same functor signature.
        int functorId = compiledClauses[0].FunctorId;
        int arity = compiledClauses[0].Arity;
        for (int i = 1; i < compiledClauses.Count; i++)
        {
            if (compiledClauses[i].FunctorId != functorId)
                throw new ArgumentException(
                    "All clauses passed to PredicateCompiler must share the same functor "
                    + $"(clause 0 = id {functorId}, clause {i} = id {compiledClauses[i].FunctorId}).");
        }

        // Per-clause source positions, in source order. Used by the
        // stack-trace path together with the Meta(DbgInfo, clauseIndex)
        // opcodes the multi-clause paths emit before each clause body.
        var clausePositions = clauses.Select(c => c.Position).ToArray();

        // Single-clause shortcut. No Meta opcode needed: any PC inside the
        // predicate is by definition inside clause 0.
        if (compiledClauses.Count == 1)
        {
            if (isDynamic)
            {
                // ADR-015 chunk C step 4: dynamic predicates run through
                // a fixed-size trampoline (enter_dynamic; execute
                // <chain-head>) — asserta patches the Execute operand
                // in place to install a new chain head. The clause itself
                // then carries try_me_else <fail-stub> (so its operand is
                // patchable for assertz too) + check_visible + body.
                var em = new BytecodeEmitter();
                em.EmitEnterDynamic();
                var dispatch = new List<int>();
                if (failStubAddr > 0)
                {
                    // Trampoline: chain-head sits right after the
                    // execute (predicate-local 6).
                    int execOpPos = em.Position;
                    em.EmitExecute(targetAddress: 6);
                    dispatch.Add(execOpPos + 1);
                    em.EmitTryMeElse(failStubAddr, arity);
                }
                em.EmitCheckVisible(born: 0L, died: long.MaxValue);
                int clauseStart = em.Position;
                em.AppendBytes(compiledClauses[0].Bytecode);
                // ADR-025 — rebase any intra-clause branch operands past the
                // trampoline + check_visible prefix.
                MergeClauseDispatchSites(em, compiledClauses[0], clauseStart, dispatch);
                var shiftedCallSites = compiledClauses[0].CallSites
                    .Select(s => new CallSite(
                        clauseStart + s.OpcodeOffset, s.CalleeFunctorId, s.IsExecute))
                    .ToArray();
                return new CompiledPredicate(
                    em.ToBytes(),
                    functorId,
                    arity,
                    clauseCount: 1,
                    callSites: shiftedCallSites,
                    dispatchSites: dispatch.ToArray(),
                    switchTables: Array.Empty<SwitchTable>(),
                    switchTableIdSites: Array.Empty<int>(),
                    sourcePosition: clauses[0].Position,
                    clauseSourcePositions: clausePositions)
                {
                    DebugStops = ShiftDebugStops(compiledClauses[0], clauseStart),   // ADR-035
                    DebugFrames = ClauseFrames(compiledClauses[0], clauseStart, 1),  // ADR-035
                };
            }
            return new CompiledPredicate(
                compiledClauses[0].Bytecode,
                functorId,
                arity,
                clauseCount: 1,
                callSites: compiledClauses[0].CallSites,
                // ADR-025 — the clause bytes ARE the predicate bytes here (no
                // prefix), so the clause's intra-clause branch operands are
                // already predicate-local: pass them straight through.
                dispatchSites: compiledClauses[0].DispatchSites,
                switchTables: Array.Empty<SwitchTable>(),
                switchTableIdSites: Array.Empty<int>(),
                sourcePosition: clauses[0].Position,
                clauseSourcePositions: clausePositions)
            {
                DebugStops = compiledClauses[0].DebugStops,   // ADR-035 — no prefix, no shift
                DebugFrames = ClauseFrames(compiledClauses[0], 0, 1),
            };
        }

        // Decide whether indexing pays off. First-arg (A1) indexing
        // (ADR-007) plus the sequential multi-arg fallback
        // — when A1 is var in the call, dispatch consults A2, then A3,
        // etc. An arg position k is indexable iff some clause has a
        // concrete (non-var, non-other) value at position k. If no arg
        // position is indexable, fall through to the plain try_me_else
        // chain.
        var perArgInfo = new ArgInfo[arity][];
        var indexableArgs = new List<int>();
        // JIT indexing: when the engine hasn't yet proven
        // this predicate hot, skip the indexability scan entirely and
        // emit the plain chain. Cold / churning dynamic predicates
        // don't pay the switch-table build cost.
        if (enableIndexing)
        {
            for (int k = 0; k < arity; k++)
            {
                perArgInfo[k] = clauses.Select(c => ClassifyArg(c, k)).ToArray();
                if (perArgInfo[k].Any(f => f.Kind != ArgKind.Var && f.Kind != ArgKind.Other))
                    indexableArgs.Add(k);
            }
        }

        // the indexed path emits enter_dynamic at the
        // entry and check_visible per clause when isDynamic, so a hot
        // dynamic predicate's runtime dispatch honours the ISO
        // logical-update view via the same mechanism the chain path
        // uses.
        //
        // every isDynamic + indexable case routes through
        // CompileIndexedDynamic, which now handles multi-arg layouts
        // too — every bucket chain across every level is extensible
        // via the try_me_else pattern. The
        // indexed fallback is only used for static predicates now.
        if (isDynamic && indexableArgs.Count > 0)
            return CompileIndexedDynamic(
                compiledClauses, perArgInfo, indexableArgs, functorId, arity,
                clauses[0].Position, clausePositions, failStubAddr, EmitDebugInfo);
        return indexableArgs.Count > 0
            ? CompileIndexed(compiledClauses, clauses, perArgInfo, indexableArgs, functorId, arity,
                             clauses[0].Position, clausePositions, isDynamic, EmitDebugInfo)
            : CompileTryMeElseChain(compiledClauses, functorId, arity,
                                    clauses[0].Position, clausePositions, isDynamic, failStubAddr,
                                    EmitDebugInfo);
    }

    /// <summary>Size of one <see cref="Opcode.Meta"/> + <see cref="MetaSubOpcode.DbgInfo"/>
    /// instruction: 1 opcode byte + 1 sub-byte + 4-byte entry id payload.
    /// gated on <see cref="EmitDebugInfo"/> — Release builds
    /// account for the absent bytes here too (otherwise the per-clause
    /// dispatch addresses wouldn't match the emitted bytecode).</summary>
    private const int MetaDbgInfoSize = 6;

    // ============================================================================
    // try_me_else / retry_me_else / trust_me chain (no first-arg indexing)
    // ============================================================================

    private static CompiledPredicate CompileTryMeElseChain(
        IReadOnlyList<CompiledClause> compiledClauses, int functorId, int arity,
        Shumway.Compiler.Lexer.SourcePosition position,
        IReadOnlyList<Shumway.Compiler.Lexer.SourcePosition> clausePositions,
        bool isDynamic = false,
        int failStubAddr = 0,
        bool emitDebugInfo = true)
    {
        int metaDbgInfoSize = emitDebugInfo ? MetaDbgInfoSize : 0;
        // Per-clause layout: dispatch instruction (try/retry/trust),
        // then Meta(DbgInfo, clauseIndex), then (for dynamic predicates)
        // a check_visible visibility filter, then the clause body. The
        // dispatch BPs point at the next clause's dispatch instruction;
        // the Meta opcode sits inside the body region so any PC inside
        // (Meta + clause body) maps back to that clause via a backward
        // scan from the runtime PC. ADR-015 chunk C: dynamic predicates
        // are prefixed by an enter_dynamic opcode at the entry, and every
        // clause begins with a check_visible (born=0, died=MaxValue at
        // this step — always visible; step 4 wires real values).
        int n = compiledClauses.Count;
        const int CheckVisibleSize = 17;
        int enterDynamicSize = isDynamic ? 1 : 0;
        int checkVisibleSize = isDynamic ? CheckVisibleSize : 0;

        // ADR-015 chunk C step 4: a dynamic chain's last clause ends with
        // retry_me_else <fail-stub> instead of trust_me, so a future
        // assertz can patch the operand to point at the new clause (same
        // size — 4-byte in-place patch). Without a fail-stub address
        // (paso-3 callers), keep trust_me.
        bool dynamicChain = isDynamic && failStubAddr > 0;

        // Trampoline (only when dynamicChain): enter_dynamic; execute
        // <chain-head>. The execute operand is patched by asserta to
        // install a new chain head; the chain-head itself sits right
        // after the trampoline.
        int trampolineExecuteSize = dynamicChain ? 5 : 0;
        int trampolineSize = enterDynamicSize + trampolineExecuteSize;

        int[] clauseBodyOffsets = new int[n];
        int pos = trampolineSize;
        for (int i = 0; i < n; i++)
        {
            int dispatchSize = DispatchSizeFor(i, n, dynamicChain);
            pos += dispatchSize;
            clauseBodyOffsets[i] = pos;
            pos += metaDbgInfoSize;
            pos += checkVisibleSize;
            pos += compiledClauses[i].Bytecode.Length;
        }

        var emitter = new BytecodeEmitter();
        var callSites = new List<CallSite>();
        var dispatchSites = new List<int>();
        var debugStops = new List<DebugStop>();   // ADR-035
        var debugFrames = new List<DebugClauseFrame>();   // ADR-035
        if (isDynamic) emitter.EmitEnterDynamic();
        if (dynamicChain)
        {
            // Trampoline's execute target is the first clause's chain
            // instruction, sitting right after the trampoline at the
            // predicate-local offset = trampolineSize. The Linker shifts
            // the operand by basePos + loadOffset to make it absolute.
            int execOpPos = emitter.Position;
            emitter.EmitExecute(targetAddress: trampolineSize);
            dispatchSites.Add(execOpPos + 1);
        }
        for (int i = 0; i < n; i++)
        {
            if (i == 0)
            {
                int nextDispatch = clauseBodyOffsets[1] - DispatchSizeFor(1, n, dynamicChain);
                int opPos = emitter.Position;
                emitter.EmitTryMeElse(nextDispatch, arity);
                dispatchSites.Add(opPos + 1);
            }
            else if (i == n - 1)
            {
                if (dynamicChain)
                {
                    // Absolute fail-stub address — NOT in dispatchSites so
                    // the linker does not shift it.
                    emitter.EmitRetryMeElse(failStubAddr);
                }
                else
                {
                    emitter.EmitTrustMe();
                }
            }
            else
            {
                int nextDispatch = clauseBodyOffsets[i + 1] - DispatchSizeFor(i + 1, n, dynamicChain);
                int opPos = emitter.Position;
                emitter.EmitRetryMeElse(nextDispatch);
                dispatchSites.Add(opPos + 1);
            }

            if (emitDebugInfo) emitter.EmitMetaDbgInfo(i);
            if (isDynamic) emitter.EmitCheckVisible(born: 0L, died: long.MaxValue);
            int clauseStart = emitter.Position;
            emitter.AppendBytes(compiledClauses[i].Bytecode);
            foreach (var site in compiledClauses[i].CallSites)
                callSites.Add(new CallSite(
                    clauseStart + site.OpcodeOffset, site.CalleeFunctorId, site.IsExecute));
            debugStops.AddRange(ShiftDebugStops(compiledClauses[i], clauseStart));   // ADR-035
            debugFrames.AddRange(ClauseFrames(compiledClauses[i], clauseStart, i + 1));   // ADR-035
            MergeClauseDispatchSites(emitter, compiledClauses[i], clauseStart, dispatchSites);
        }

        return new CompiledPredicate(
            emitter.ToBytes(), functorId, arity, n, callSites, dispatchSites,
            Array.Empty<SwitchTable>(), Array.Empty<int>(), position,
            clausePositions)
        {
            DebugStops = debugStops,   // ADR-035
            DebugFrames = debugFrames,   // ADR-035
        };
    }

    private static int DispatchSizeFor(int clauseIndex, int totalClauses, bool dynamicChain = false) =>
        clauseIndex == 0
            ? 9                                          // try_me_else: opcode + addr + arity
            : clauseIndex == totalClauses - 1 && !dynamicChain
                ? 1                                      // trust_me
                : 5;                                     // retry_me_else: opcode + addr

    /// <summary>ADR-025 — folds a clause's intra-clause branch operands (the
    /// inline-ITE <c>try_me_else</c>/<c>jump</c> targets) into the predicate's
    /// dispatch sites: each site offset shifts by the clause's placement, and the
    /// operand VALUE is rebased from clause-local to predicate-local (the linker
    /// then shifts it to program-absolute like any other dispatch site).</summary>
    /// <summary>ADR-035 — a clause's frame map, placed where the clause was placed.
    /// The span is what takes a debugger from a program address back to the clause
    /// executing there, and so to the names of the variables in its frame.</summary>
    private static IReadOnlyList<DebugClauseFrame> ClauseFrames(
        CompiledClause clause, int clauseStart, int clauseNumber)
    {
        // Having somewhere to STOP and having variables to SHOW are different things, and
        // reading the first as a proxy for the second cost the query frame its variables:
        // the `__query__` wrapper is deliberately given no stop sites (the user cannot set a
        // breakpoint on a line they never wrote), so it fell out here — and a debugger
        // stopped in `?- X = 41, debugger_break.` could not show X, the one variable the user
        // was looking at. A clause is debuggable if it was COMPILED debuggable, which is what
        // having either of these says.
        if (clause.DebugStops.Count == 0 && clause.DebugVariables.Count == 0)
            return Array.Empty<DebugClauseFrame>();
        return new[]
        {
            new DebugClauseFrame(
                clauseStart,
                clauseStart + clause.Bytecode.Length,
                clause.HasFrame,
                clause.DebugVariables)
            {
                HeadArgs = clause.DebugHeadArgs,
                ClauseNumber = clauseNumber,
            },
        };
    }

    /// <summary>ADR-035 — a clause's stop sites, shifted from clause-local offsets
    /// to predicate-local ones by where the clause was placed. Same treatment as
    /// its call sites, and for the same reason.</summary>
    private static IReadOnlyList<DebugStop> ShiftDebugStops(CompiledClause clause, int clauseStart)
    {
        if (clause.DebugStops.Count == 0) return Array.Empty<DebugStop>();
        var shifted = new DebugStop[clause.DebugStops.Count];
        for (int i = 0; i < shifted.Length; i++)
            shifted[i] = new DebugStop(
                clauseStart + clause.DebugStops[i].Offset, clause.DebugStops[i].SiteId);
        return shifted;
    }

    private static void MergeClauseDispatchSites(
        BytecodeEmitter emitter, CompiledClause clause, int clauseStart, List<int> dispatchSites)
    {
        foreach (int site in clause.DispatchSites)
        {
            int local = Shumway.Core.BytecodeIO.ReadInt32(clause.Bytecode, site);
            emitter.PatchInt32(clauseStart + site, clauseStart + local);
            dispatchSites.Add(clauseStart + site);
        }
    }

    // ============================================================================
    // Indexing (ADR-007 first-arg + sequential multi-arg fallback)
    // ============================================================================

    private enum ArgKind { Var, Atom, Int, List, Struct, Other }

    private readonly record struct ArgInfo(ArgKind Kind, int Key);

    /// <summary>Classifies <paramref name="clause"/>'s argument at index
    /// <paramref name="argIdx"/> for indexing purposes. Mirrors the
    /// ADR-007 classification but parameterised on the arg position so
    /// the multi-arg scheduler can build per-position buckets.</summary>
    private static ArgInfo ClassifyArg(Clause clause, int argIdx)
    {
        // For a Rule the clause Term is `:-/2` with head at Args[0]; for a Fact
        // the clause Term IS the head.
        Term headTerm = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        if (headTerm is not CompoundTerm compound || argIdx >= compound.Args.Length)
            return new ArgInfo(ArgKind.Var, 0);

        return compound.Args[argIdx] switch
        {
            VarTerm => new ArgInfo(ArgKind.Var, 0),
            AtomTerm a => new ArgInfo(
                ArgKind.Atom, AtomTable.Intern(a.Name, permanent: true).Id),
            IntTerm n when n.Value >= int.MinValue && n.Value <= int.MaxValue
                => new ArgInfo(ArgKind.Int, (int)n.Value),
            CompoundTerm c when c.Functor == "." && c.Args.Length == 2
                => new ArgInfo(ArgKind.List, 0),
            CompoundTerm c => new ArgInfo(
                ArgKind.Struct,
                FunctorTable.Intern(
                    AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length)),
            // FloatTerm / StringTerm / IntTerm out of int range / etc. behave
            // like a variable for indexing purposes — they fall through to the
            // full chain.
            _ => new ArgInfo(ArgKind.Other, 0),
        };
    }

    /// <summary>Per-arg-level bucket data computed in pass 1 and consumed
    /// by passes 2 and 3 of <see cref="CompileIndexed"/>.</summary>
    private sealed class ArgLevel
    {
        public int ArgIdx;
        public List<int> VarIdxs = new();
        public SortedDictionary<int, List<int>> AtomMap = new();
        public SortedDictionary<int, List<int>> IntMap = new();
        public List<int> ListIdxs = new();
        public SortedDictionary<int, List<int>> StructMap = new();

        // Merged-with-var buckets (what actually gets dispatched into).
        public Dictionary<int, List<int>> AtomBuckets = new();
        public Dictionary<int, List<int>> IntBuckets = new();
        public List<int> ListBucket = new();
        public Dictionary<int, List<int>> StructBuckets = new();

        public bool HasAtoms => AtomBuckets.Count > 0;
        public bool HasInts => IntBuckets.Count > 0;
        public bool HasStructs => StructBuckets.Count > 0;

        // Positions resolved in pass 1.
        public int SwitchPos;          // start of switch_on_term / switch_on_arg
        public int VarLblPos;          // var-fallthrough target (next level or final chain)
        // Chain over ONLY the var-headed clauses at this position — the
        // correct target for a const/list/struct call value that has no
        // specific bucket. (The full chain in VarLblPos is only right
        // for an unbound call arg, which can match every clause.) Using
        // this instead of VarLblPos for empty categories keeps e.g.
        // member's final element deterministic: `[]` routes to the
        // const label, whose only candidate is the var-headed base
        // clause, so no choice point is left. Only populated for the
        // single-indexable-arg case.
        public int VarOnlyChainPos;
        public int ConstLblPos;        // start of switch_on_atom (or _arg)
        public int ListLblPos;
        public int StructLblPos;
        public int SwitchOnAtomPos;
        public int SwitchOnIntegerPos;
        public int SwitchOnStructurePos;
        public int AtomTableId = -1;
        public int IntTableId = -1;
        public int StructTableId = -1;
        public Dictionary<int, int> AtomGroupPos = new();
        public Dictionary<int, int> IntGroupPos = new();
        public Dictionary<int, int> StructGroupPos = new();

        // ADR-027/028 nested indexing. A bucket's linear chain is replaced by a
        // nested SubSwitch (sibling-arg or sub-path). ListSub for the list bucket;
        // the *GroupSub dicts, keyed by the bucket's value key, for the typed
        // value buckets.
        public SubSwitch? ListSub;
        public Dictionary<int, SubSwitch> StructGroupSub = new();
        public Dictionary<int, SubSwitch> AtomGroupSub = new();
        public Dictionary<int, SubSwitch> IntGroupSub = new();
    }

    // ============================================================================
    // ADR-027 — second-level (sub-argument) indexing
    // ============================================================================

    /// <summary>A sub-argument switch that replaces a bucket's linear chain: it
    /// dispatches on a sub-term reached by a bounded path (<see cref="Sub0"/>,
    /// then <see cref="Sub1"/> if &gt;= 0) from argument <see cref="ArgIdx"/>.
    /// Structurally a nested copy of the top-level typed switch (a keyed table +
    /// per-key group chains + a default chain over the wildcard clauses).</summary>
    // ADR-028 generalises the ADR-027 SubSwitch into a nested BucketSwitch that
    // replaces ANY >= 2-clause value-bucket chain, discriminating by a
    // (dimension, key-kind): the dimension is either a SIBLING argument (read
    // X[SiblingArg] via switch_on_{atom,integer,structure}_arg, 9 bytes) or a
    // SUB-path (walk from X[ArgIdx] via switch_on_{atom,integer,structure}_sub,
    // 17 bytes, ADR-027); the key-kind is atom / integer / structure functor.
    private sealed class SubSwitch
    {
        // Dimension.
        public bool IsSibling;        // true => sibling arg; false => sub-path
        public int SiblingArg;        // sibling dimension: the argument to read
        public int ArgIdx;            // sub dimension: the bucketed arg to walk from
        public int Sub0;
        public int Sub1;              // -1 = depth-1

        public BucketKeyKind Kind;
        public SortedDictionary<int, List<int>> Buckets = new();  // key -> {ground ∪ var-wildcards}
        // The FULL bucket in source order — the target when the discriminator is
        // unbound (Ref) or its key misses. An unbound discriminator can unify
        // with every clause in the bucket, so the default MUST try them all
        // (ADR-028 soundness fix; ADR-027's wildcards-only default dropped the
        // ground clauses when a var-headed clause was present).
        public List<int> AllClauses = new();

        // Positions resolved during layout.
        public int SwitchPos;
        public int TableId = -1;
        public readonly Dictionary<int, int> GroupPos = new();    // key -> chain pos (buckets >= 2)
        public int DefaultChainPos = -1;                          // full-bucket chain (>= 2), else -1

        public int SwitchByteSize => IsSibling ? 9 : 17;
    }

    private enum SubKeyKind { Var, Atom, Int, Struct }
    private enum BucketKeyKind { Atom, Int, Struct }

    /// <summary>Classifies the sub-term of <paramref name="clause"/>'s
    /// <paramref name="argIdx"/> head argument reached by <paramref name="path"/>
    /// (a list <c>'.'/2</c> exposes head=0/tail=1; a struct exposes its args).
    /// A var terminal, or any position that can't be followed at compile time
    /// (a differently-shaped head), classifies as <see cref="SubKeyKind.Var"/> —
    /// a wildcard that matches every key (sound over-approximation).</summary>
    private static (SubKeyKind Kind, int Key) ClassifySubPath(
        Clause clause, int argIdx, ReadOnlySpan<int> path)
    {
        Term headTerm = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        if (headTerm is not CompoundTerm compound || argIdx >= compound.Args.Length)
            return (SubKeyKind.Var, 0);
        Term cur = compound.Args[argIdx];
        foreach (int idx in path)
        {
            Term? next = StepInto(cur, idx);
            if (next is null) return (SubKeyKind.Var, 0);   // can't follow -> wildcard
            cur = next;
        }
        return SubKeyOf(cur, listAsStruct: true);
    }

    /// <summary>Classifies a terminal term as an indexing key. <paramref
    /// name="listAsStruct"/> true (sub-path dimension — <c>switch_on_structure_sub</c>
    /// handles a nested list) keys a list as the cons functor; false (sibling
    /// dimension — <c>switch_on_structure_arg</c> handles only <c>Tag.Str</c>)
    /// treats a list as a var wildcard. A float/string is always a wildcard.</summary>
    private static (SubKeyKind Kind, int Key) SubKeyOf(Term cur, bool listAsStruct)
    {
        switch (cur)
        {
            case AtomTerm a:
                return (SubKeyKind.Atom, AtomTable.Intern(a.Name, permanent: true).Id);
            case IntTerm n when n.Value >= int.MinValue && n.Value <= int.MaxValue:
                return (SubKeyKind.Int, (int)n.Value);
            case CompoundTerm c when c.Functor == "." && c.Args.Length == 2:
                return listAsStruct
                    ? (SubKeyKind.Struct, FunctorTable.Intern(AtomTable.Intern(".", permanent: true).Id, 2))
                    : (SubKeyKind.Var, 0);
            case CompoundTerm c:
                return (SubKeyKind.Struct,
                    FunctorTable.Intern(AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length));
            default:
                return (SubKeyKind.Var, 0);   // var / float / string -> wildcard
        }
    }

    /// <summary>Classifies clause <paramref name="clause"/>'s head argument
    /// <paramref name="argPos"/> as a sibling-dimension key (a list is a wildcard —
    /// see <see cref="SubKeyOf"/>). Out-of-range / non-compound head -> var.</summary>
    private static (SubKeyKind Kind, int Key) ClassifyHeadArg(Clause clause, int argPos)
    {
        Term headTerm = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        if (headTerm is not CompoundTerm compound || argPos >= compound.Args.Length)
            return (SubKeyKind.Var, 0);
        return SubKeyOf(compound.Args[argPos], listAsStruct: false);
    }

    /// <summary>One compile-time hop into a head term: a list <c>'.'/2</c>
    /// exposes head (0) / tail (1); any other compound exposes <c>Args[idx]</c>.
    /// Returns null when the term is not a compound or the index is out of
    /// range.</summary>
    private static Term? StepInto(Term t, int idx)
    {
        if (t is CompoundTerm c)
        {
            if (c.Functor == "." && c.Args.Length == 2)
                return idx is 0 or 1 ? c.Args[idx] : null;
            return idx >= 0 && idx < c.Args.Length ? c.Args[idx] : null;
        }
        return null;
    }

    /// <summary>ADR-028: build the best nested <see cref="SubSwitch"/> for a
    /// ≥ 2-clause value bucket. Considers the SIBLING dimension (read
    /// <paramref name="siblingArgs"/>) and — when <paramref name="subPaths"/> is
    /// non-null (a list / struct bucket) — the SUB dimension (walk from
    /// <paramref name="bucketArgIdx"/>). Each candidate partitions the bucket by a
    /// single homogeneous key-kind (all atoms, all integers, or all struct
    /// functors) with ≥ 2 distinct keys; clauses whose position is a var /
    /// unfollowable are wildcards (merged into every keyed bucket). The candidate
    /// with the smallest worst key-group wins (tie: sibling before sub, then lower
    /// index). Returns null when nothing partitions.</summary>
    private static SubSwitch? TryBuildBucketSwitch(
        int bucketArgIdx, IReadOnlyList<int> clauseSet, IReadOnlyList<Clause> clauses,
        IEnumerable<(int Sub0, int Sub1)>? subPaths, IReadOnlyList<int> siblingArgs)
    {
        SubSwitch? best = null;
        int bestWorst = int.MaxValue;

        // Sibling candidates first (cheaper dispatch, 9 vs 17 bytes; tie-break).
        foreach (int j in siblingArgs)
            Consider(ci => ClassifyHeadArg(clauses[ci], j),
                     s => { s.IsSibling = true; s.SiblingArg = j; });

        if (subPaths is not null)
            foreach (var (sub0, sub1) in subPaths)
            {
                int s0 = sub0, s1 = sub1;
                Consider(ci => ClassifySubPath(clauses[ci], bucketArgIdx,
                                   s1 >= 0 ? new[] { s0, s1 } : new[] { s0 }),
                         s => { s.ArgIdx = bucketArgIdx; s.Sub0 = s0; s.Sub1 = s1; });
            }

        return best;

        // Classify the bucket at one position, build the homogeneous-kind
        // partition (if any ≥ 2-distinct-key kind exists), and keep it if it beats
        // the incumbent worst-group.
        void Consider(Func<int, (SubKeyKind Kind, int Key)> keyOf, Action<SubSwitch> setDim)
        {
            var atom = new SortedDictionary<int, List<int>>();
            var intd = new SortedDictionary<int, List<int>>();
            var strd = new SortedDictionary<int, List<int>>();
            var wild = new List<int>();
            foreach (int ci in clauseSet)
            {
                var (kind, key) = keyOf(ci);
                switch (kind)
                {
                    case SubKeyKind.Atom:   GetOrAdd(atom, key).Add(ci); break;
                    case SubKeyKind.Int:    GetOrAdd(intd, key).Add(ci); break;
                    case SubKeyKind.Struct: GetOrAdd(strd, key).Add(ci); break;
                    default:                wild.Add(ci); break;
                }
            }
            // A single homogeneous ground kind with ≥ 2 distinct keys (a mixed
            // position can't drive one typed value table — deferred).
            int kinds = (atom.Count > 0 ? 1 : 0) + (intd.Count > 0 ? 1 : 0) + (strd.Count > 0 ? 1 : 0);
            if (kinds != 1) return;
            BucketKeyKind kk;
            SortedDictionary<int, List<int>> ground;
            if (atom.Count >= 2) { kk = BucketKeyKind.Atom; ground = atom; }
            else if (intd.Count >= 2) { kk = BucketKeyKind.Int; ground = intd; }
            else if (strd.Count >= 2) { kk = BucketKeyKind.Struct; ground = strd; }
            else return;

            int worst = 0;
            foreach (var g in ground.Values) worst = Math.Max(worst, g.Count + wild.Count);
            if (worst >= bestWorst) return;   // strictly better only (sibling probed first wins ties)

            var ss = new SubSwitch { Kind = kk, AllClauses = clauseSet.ToList() };
            setDim(ss);
            foreach (var (key, specifics) in ground)
            {
                var merged = new SortedSet<int>(specifics);
                foreach (int w in wild) merged.Add(w);
                ss.Buckets[key] = merged.ToList();
            }
            best = ss;
            bestWorst = worst;
        }
    }

    /// <summary>Candidate paths for a LIST bucket: the head (depth-1), then each
    /// sub-position of the head compound (depth-2, the Arity token-stream
    /// <c>[t(Sym,Code)|_]</c> idiom).</summary>
    private static IEnumerable<(int, int)> ListCandidatePaths()
    {
        yield return (0, -1);
        for (int j = 0; j < MaxSubArityProbe; j++) yield return (0, j);
    }

    /// <summary>Candidate paths for a STRUCT functor group: each argument of the
    /// struct (depth-1). The arg is the struct itself, so the first hop indexes
    /// straight into it.</summary>
    private static IEnumerable<(int, int)> StructCandidatePaths()
    {
        for (int j = 0; j < MaxSubArityProbe; j++) yield return (j, -1);
    }

    private const int MaxSubArityProbe = 8;

    private static int SubChainSize(int count) => count switch
    {
        0 => 0,
        1 => 0,
        _ => 9 + 5 * (count - 1),   // try(9) + (count-2)*retry(5) + trust(5)
    };

    /// <summary>Lays out a nested-switch region starting at <paramref name="pos"/>:
    /// the switch opcode (9 bytes sibling / 17 bytes sub), a group chain for every
    /// bucket with ≥ 2 clauses, then the full-bucket default chain (the unbound /
    /// missing-key target — ADR-028 soundness). Returns the position just past the
    /// region.</summary>
    private static int LayoutSubSwitch(SubSwitch ss, int pos)
    {
        ss.SwitchPos = pos;
        pos += ss.SwitchByteSize;
        foreach (var (key, group) in ss.Buckets)
            if (group.Count >= 2) { ss.GroupPos[key] = pos; pos += SubChainSize(group.Count); }
        if (ss.AllClauses.Count >= 2) { ss.DefaultChainPos = pos; pos += SubChainSize(ss.AllClauses.Count); }
        return pos;
    }

    private static CompiledPredicate CompileIndexed(
        IReadOnlyList<CompiledClause> compiledClauses,
        IReadOnlyList<Clause> clauses,
        ArgInfo[][] perArgInfo,
        List<int> indexableArgs,
        int functorId,
        int arity,
        Shumway.Compiler.Lexer.SourcePosition position,
        IReadOnlyList<Shumway.Compiler.Lexer.SourcePosition> clausePositions,
        bool isDynamic = false,
        bool emitDebugInfo = true)
    {
        int metaDbgInfoSize = emitDebugInfo ? MetaDbgInfoSize : 0;
        int n = compiledClauses.Count;
        // dynamic indexed predicates wrap their entry in
        // enter_dynamic (samples DbGeneration into CurrentViewGen) and
        // gate every clause body with check_visible (filters by born/
        // died vs the captured view-gen), the same ADR-015 chunk-C
        // mechanism the non-indexed chain path uses. Static indexed
        // predicates skip both — they're immutable, no view to check.
        const int CheckVisibleSize = 17;
        int enterDynamicSize = isDynamic ? 1 : 0;
        int checkVisibleSize = isDynamic ? CheckVisibleSize : 0;

        // ----- Bucketise -----

        // ----- Build per-arg-level buckets -----

        var levels = new ArgLevel[indexableArgs.Count];
        for (int li = 0; li < indexableArgs.Count; li++)
        {
            int k = indexableArgs[li];
            var lvl = new ArgLevel { ArgIdx = k };
            for (int i = 0; i < n; i++)
            {
                ArgInfo info = perArgInfo[k][i];
                switch (info.Kind)
                {
                    case ArgKind.Var:   lvl.VarIdxs.Add(i); break;
                    case ArgKind.Atom:  GetOrAdd(lvl.AtomMap, info.Key).Add(i); break;
                    case ArgKind.Int:   GetOrAdd(lvl.IntMap, info.Key).Add(i); break;
                    case ArgKind.List:  lvl.ListIdxs.Add(i); break;
                    case ArgKind.Struct:GetOrAdd(lvl.StructMap, info.Key).Add(i); break;
                    case ArgKind.Other: lvl.VarIdxs.Add(i); break;
                }
            }
            // Var-arg-at-this-position clauses match every concrete value;
            // they're tried alongside the type-specific clauses in every
            // bucket.
            List<int> MergeWithVar(List<int> specifics)
            {
                var seen = new HashSet<int>(specifics);
                foreach (int v in lvl.VarIdxs) seen.Add(v);
                var merged = seen.ToList();
                merged.Sort();
                return merged;
            }
            lvl.AtomBuckets = lvl.AtomMap.ToDictionary(kv => kv.Key, kv => MergeWithVar(kv.Value));
            lvl.IntBuckets = lvl.IntMap.ToDictionary(kv => kv.Key, kv => MergeWithVar(kv.Value));
            lvl.StructBuckets = lvl.StructMap.ToDictionary(kv => kv.Key, kv => MergeWithVar(kv.Value));
            lvl.ListBucket = MergeWithVar(lvl.ListIdxs);
            levels[li] = lvl;
        }

        // ADR-027/028: nested switches inside a value bucket. A ≥ 2-clause bucket
        // whose clauses share the bucketed arg's key but differ at a sibling arg
        // (any bucket) or at a sub-arg of a list/struct value (list/struct
        // buckets) is replaced by a nested SubSwitch. The bucketed arg itself is
        // never a useful sibling discriminator (all clauses share its key), so it
        // is excluded from the sibling set.
        for (int li = 0; li < levels.Length; li++)
        {
            var lvl = levels[li];
            // A value bucket at level li is reached with the EARLIER cascade args
            // (levels 0..li-1) unbound — the var-fallthrough path led here — and
            // this level's arg pinned to the bucket key. So the only args that can
            // be bound at the call, and thus discriminate the bucket, are the
            // LATER cascade args (levels li+1..). Earlier / this-level args would
            // always miss to the default.
            var siblings = new List<int>();
            for (int lj = li + 1; lj < levels.Length; lj++) siblings.Add(levels[lj].ArgIdx);

            if (lvl.ListBucket.Count >= 2)
                lvl.ListSub = TryBuildBucketSwitch(
                    lvl.ArgIdx, lvl.ListBucket, clauses, ListCandidatePaths(), siblings);
            foreach (var (key, group) in lvl.StructBuckets)
                if (group.Count >= 2)
                {
                    var ss = TryBuildBucketSwitch(lvl.ArgIdx, group, clauses, StructCandidatePaths(), siblings);
                    if (ss != null) lvl.StructGroupSub[key] = ss;
                }
            // ADR-028: atom / integer value buckets — sibling dimension only (no
            // sub-path into a scalar value). Gated at ≥ 3: a 2-clause bucket ends
            // in `trust` (no leftover choice point), so the only gain is one
            // skipped head-unify — not worth the nested switch's code size (the
            // audit treats worst-bucket ≤ 2 as already well-indexed).
            foreach (var (key, group) in lvl.AtomBuckets)
                if (group.Count >= 3)
                {
                    var ss = TryBuildBucketSwitch(lvl.ArgIdx, group, clauses, subPaths: null, siblings);
                    if (ss != null) lvl.AtomGroupSub[key] = ss;
                }
            foreach (var (key, group) in lvl.IntBuckets)
                if (group.Count >= 3)
                {
                    var ss = TryBuildBucketSwitch(lvl.ArgIdx, group, clauses, subPaths: null, siblings);
                    if (ss != null) lvl.IntGroupSub[key] = ss;
                }
        }

        // ----- Pass 1: layout offsets -----

        static int ChainSize(int count) => count switch
        {
            0 => 0,
            1 => 0,             // single-target buckets jump directly to the clause body
            _ => 9 + 5 * (count - 1),    // try(9) + (count-2)*retry(5) + trust(5)
        };
        static int SwitchSize(int argIdx) => argIdx == 0 ? 17 : 21;
        static int SubDispatchSize(int argIdx) => argIdx == 0 ? 5 : 9;

        // start the predicate-local layout after the
        // enter_dynamic byte when this is a dynamic predicate. Every
        // switch / chain / body offset shifts up by 1; targets stored
        // in switch tables and try/retry/trust addresses are
        // predicate-local so they remain correct under the shift.
        int pos = enterDynamicSize;

        // Top-level: one switch_on_term (arg 0) or switch_on_arg (arg k > 0)
        // per indexable arg, chained head-to-tail. Each switch's var label
        // jumps to the next switch (or to the final chain if last).
        for (int li = 0; li < levels.Length; li++)
        {
            levels[li].SwitchPos = pos;
            pos += SwitchSize(levels[li].ArgIdx);
        }

        // Final chain (try/retry/trust over all clauses in source order).
        // This is the var-fallthrough target of the LAST indexable arg.
        int finalChainPos = pos;
        pos += ChainSize(n);

        // Var-only chain (single-indexable-arg case): try/retry/trust
        // over just the var-headed clauses. The correct fallthrough for
        // a const/list/struct call value with no specific bucket. -1 =
        // "patch to the single var clause's body later"; only allocated
        // when there are 2+ var clauses. When there are no var clauses
        // it falls back to the full chain (heads fail there anyway).
        bool singleLevel = levels.Length == 1;
        if (singleLevel)
        {
            int vc = levels[0].VarIdxs.Count;
            if (vc == 0)      levels[0].VarOnlyChainPos = finalChainPos;
            else if (vc == 1) levels[0].VarOnlyChainPos = -2;   // patched below (distinct from list-bucket -1)
            else              { levels[0].VarOnlyChainPos = pos; pos += ChainSize(vc); }
        }

        // Wire var labels: each level's var-fallthrough points to the next
        // level's switch, or to the final chain when it's the last level.
        for (int li = 0; li < levels.Length; li++)
            levels[li].VarLblPos = li + 1 < levels.Length
                ? levels[li + 1].SwitchPos
                : finalChainPos;

        // Per-level sub-dispatches and bucket chains.
        for (int li = 0; li < levels.Length; li++)
        {
            var lvl = levels[li];
            // Empty-category fallthrough: the var-only chain in the
            // single-arg case, else the full var chain (multi-arg keeps
            // the next-level dispatch semantics on VarLblPos).
            int emptyFallthrough = singleLevel ? lvl.VarOnlyChainPos : lvl.VarLblPos;
            int sub = SubDispatchSize(lvl.ArgIdx);
            if (lvl.HasAtoms)    { lvl.SwitchOnAtomPos = pos;      pos += sub; }
            if (lvl.HasInts)     { lvl.SwitchOnIntegerPos = pos;   pos += sub; }
            lvl.ConstLblPos = lvl.HasAtoms ? lvl.SwitchOnAtomPos
                            : lvl.HasInts  ? lvl.SwitchOnIntegerPos
                                           : emptyFallthrough;

            if (lvl.ListBucket.Count == 0)      lvl.ListLblPos = emptyFallthrough;
            else if (lvl.ListSub != null)       { pos = LayoutSubSwitch(lvl.ListSub, pos); lvl.ListLblPos = lvl.ListSub.SwitchPos; }
            else if (lvl.ListBucket.Count == 1) lvl.ListLblPos = -1;  // patched after clause body offsets are known
            else                                { lvl.ListLblPos = pos; pos += ChainSize(lvl.ListBucket.Count); }

            if (lvl.HasStructs) { lvl.SwitchOnStructurePos = pos;  pos += sub; }
            lvl.StructLblPos = lvl.HasStructs ? lvl.SwitchOnStructurePos : emptyFallthrough;

            foreach (var (key, group) in lvl.AtomBuckets)
                if (group.Count >= 2)
                {
                    if (lvl.AtomGroupSub.TryGetValue(key, out var ss))
                    { pos = LayoutSubSwitch(ss, pos); lvl.AtomGroupPos[key] = ss.SwitchPos; }
                    else { lvl.AtomGroupPos[key] = pos; pos += ChainSize(group.Count); }
                }
            foreach (var (key, group) in lvl.IntBuckets)
                if (group.Count >= 2)
                {
                    if (lvl.IntGroupSub.TryGetValue(key, out var ss))
                    { pos = LayoutSubSwitch(ss, pos); lvl.IntGroupPos[key] = ss.SwitchPos; }
                    else { lvl.IntGroupPos[key] = pos; pos += ChainSize(group.Count); }
                }
            foreach (var (key, group) in lvl.StructBuckets)
                if (group.Count >= 2)
                {
                    if (lvl.StructGroupSub.TryGetValue(key, out var ss))
                    { pos = LayoutSubSwitch(ss, pos); lvl.StructGroupPos[key] = ss.SwitchPos; }
                    else { lvl.StructGroupPos[key] = pos; pos += ChainSize(group.Count); }
                }
        }

        int[] clauseBodyPos = new int[n];
        for (int i = 0; i < n; i++)
        {
            // Each clause body is preceded by a Meta(DbgInfo, i) opcode
            // so the stack-trace path can map any PC inside the clause
            // back to its source position. Dispatch targets in switch
            // tables and try/retry/trust addresses point at the Meta
            // opcode; the runtime executes the no-op Meta then the body.
            clauseBodyPos[i] = pos;
            pos += metaDbgInfoSize;
            // every dynamic clause carries its own
            // check_visible immediately after the Meta marker, so any
            // dispatch path (switch table direct jump, bucket chain,
            // var-fallthrough chain) runs the visibility filter before
            // entering the body.
            pos += checkVisibleSize;
            pos += compiledClauses[i].Bytecode.Length;
        }
        // Resolve any single-clause list-bucket direct-jump addresses.
        for (int li = 0; li < levels.Length; li++)
            if (levels[li].ListBucket.Count == 1)
                levels[li].ListLblPos = clauseBodyPos[levels[li].ListBucket[0]];

        // Resolve the single-var-clause var-only sentinel (-2): the
        // chain is just that clause's body. Empty-category labels that
        // adopted the sentinel get the same target.
        if (singleLevel && levels[0].VarOnlyChainPos == -2)
        {
            int target = clauseBodyPos[levels[0].VarIdxs[0]];
            levels[0].VarOnlyChainPos = target;
            if (levels[0].ConstLblPos == -2)  levels[0].ConstLblPos = target;
            if (levels[0].ListLblPos == -2)   levels[0].ListLblPos = target;
            if (levels[0].StructLblPos == -2) levels[0].StructLblPos = target;
        }

        // ----- Pass 2: build switch tables (predicate-local addresses) -----

        SwitchTable BuildTable(
            IReadOnlyDictionary<int, List<int>> buckets,
            IReadOnlyDictionary<int, int> groupPos,
            int defaultAddr)
        {
            var keys = new int[buckets.Count];
            var values = new int[buckets.Count];
            int idx = 0;
            foreach (var (key, group) in buckets)
            {
                keys[idx] = key;
                values[idx] = group.Count == 1
                    ? clauseBodyPos[group[0]]
                    : groupPos[key];
                idx++;
            }
            return new SwitchTable(keys, values, defaultAddr);
        }

        // ADR-027: a sub-switch table. Single-clause buckets jump straight to the
        // body; a missed key (or a var/unfollowable sub-cell) takes the wildcard
        // chain, or the level's miss target when there are no wildcards.
        SwitchTable BuildSubTable(SubSwitch ss, int missAddr)
        {
            var keys = new int[ss.Buckets.Count];
            var values = new int[ss.Buckets.Count];
            int idx = 0;
            foreach (var (key, group) in ss.Buckets)
            {
                keys[idx] = key;
                values[idx] = group.Count == 1 ? clauseBodyPos[group[0]] : ss.GroupPos[key];
                idx++;
            }
            // ADR-028: default (unbound / missing discriminator) = the FULL bucket
            // chain (every clause can unify with an unbound discriminator), not
            // just the wildcards.
            int def = ss.AllClauses.Count >= 2 ? ss.DefaultChainPos
                    : ss.AllClauses.Count == 1 ? clauseBodyPos[ss.AllClauses[0]]
                    : missAddr;
            return new SwitchTable(keys, values, def);
        }

        var switchTables = new List<SwitchTable>();
        for (int li = 0; li < levels.Length; li++)
        {
            var lvl = levels[li];
            // An unmatched const/int/struct value matches only the
            // var-headed clauses (single-arg case) — not the full chain.
            int miss = singleLevel ? lvl.VarOnlyChainPos : lvl.VarLblPos;
            if (lvl.HasAtoms)
            {
                int defaultAddr = lvl.HasInts ? lvl.SwitchOnIntegerPos : miss;
                lvl.AtomTableId = switchTables.Count;
                switchTables.Add(BuildTable(lvl.AtomBuckets, lvl.AtomGroupPos, defaultAddr));
            }
            if (lvl.HasInts)
            {
                lvl.IntTableId = switchTables.Count;
                switchTables.Add(BuildTable(lvl.IntBuckets, lvl.IntGroupPos, miss));
            }
            if (lvl.HasStructs)
            {
                lvl.StructTableId = switchTables.Count;
                switchTables.Add(BuildTable(lvl.StructBuckets, lvl.StructGroupPos, miss));
            }
            // ADR-027/028 nested-switch tables (list / struct / atom / int buckets).
            if (lvl.ListSub != null)
            {
                lvl.ListSub.TableId = switchTables.Count;
                switchTables.Add(BuildSubTable(lvl.ListSub, miss));
            }
            foreach (var ss in lvl.StructGroupSub.Values)
            {
                ss.TableId = switchTables.Count;
                switchTables.Add(BuildSubTable(ss, miss));
            }
            foreach (var ss in lvl.AtomGroupSub.Values)
            {
                ss.TableId = switchTables.Count;
                switchTables.Add(BuildSubTable(ss, miss));
            }
            foreach (var ss in lvl.IntGroupSub.Values)
            {
                ss.TableId = switchTables.Count;
                switchTables.Add(BuildSubTable(ss, miss));
            }
        }

        // ----- Pass 3: emit bytecode -----

        var emitter = new BytecodeEmitter();
        var callSites = new List<CallSite>();
        var dispatchSites = new List<int>();
        var debugStops = new List<DebugStop>();   // ADR-035
        var debugFrames = new List<DebugClauseFrame>();   // ADR-035
        var switchTableIdSites = new List<int>();

        // ADR-027: emit a sub-switch region — the switch opcode followed by its
        // per-key group chains (buckets ≥ 2) and default chain (wildcards ≥ 2),
        // contiguously and in the same order LayoutSubSwitch reserved them.
        void EmitSubSwitch(SubSwitch ss)
        {
            if (ss.IsSibling)
            {
                switchTableIdSites.Add(emitter.Position + 5);   // op + argIdx
                switch (ss.Kind)
                {
                    case BucketKeyKind.Atom:   emitter.EmitSwitchOnAtomArg(ss.SiblingArg, ss.TableId); break;
                    case BucketKeyKind.Int:    emitter.EmitSwitchOnIntegerArg(ss.SiblingArg, ss.TableId); break;
                    default:                   emitter.EmitSwitchOnStructureArg(ss.SiblingArg, ss.TableId); break;
                }
            }
            else
            {
                switchTableIdSites.Add(emitter.Position + 13);   // op+argIdx+sub0+sub1
                switch (ss.Kind)
                {
                    case BucketKeyKind.Atom:   emitter.EmitSwitchOnAtomSub(ss.ArgIdx, ss.Sub0, ss.Sub1, ss.TableId); break;
                    case BucketKeyKind.Int:    emitter.EmitSwitchOnIntegerSub(ss.ArgIdx, ss.Sub0, ss.Sub1, ss.TableId); break;
                    default:                   emitter.EmitSwitchOnStructureSub(ss.ArgIdx, ss.Sub0, ss.Sub1, ss.TableId); break;
                }
            }
            foreach (var (key, group) in ss.Buckets)
                if (group.Count >= 2)
                    EmitChain(emitter, group, clauseBodyPos, arity, dispatchSites);
            if (ss.AllClauses.Count >= 2)
                EmitChain(emitter, ss.AllClauses, clauseBodyPos, arity, dispatchSites);
        }

        // emit enter_dynamic at the very entry of every
        // dynamic indexed predicate. Captures DbGeneration into
        // CurrentViewGen so the per-clause check_visible below filters
        // against a stable view of the database for the duration of
        // this call.
        if (isDynamic) emitter.EmitEnterDynamic();

        // 3a — top-level switch chain (one per indexable arg).
        for (int li = 0; li < levels.Length; li++)
        {
            var lvl = levels[li];
            int start = emitter.Position;
            if (lvl.ArgIdx == 0)
            {
                emitter.EmitSwitchOnTerm(lvl.VarLblPos, lvl.ConstLblPos, lvl.ListLblPos, lvl.StructLblPos);
                dispatchSites.Add(start + 1);
                dispatchSites.Add(start + 5);
                dispatchSites.Add(start + 9);
                dispatchSites.Add(start + 13);
            }
            else
            {
                emitter.EmitSwitchOnArg(lvl.ArgIdx, lvl.VarLblPos, lvl.ConstLblPos, lvl.ListLblPos, lvl.StructLblPos);
                // arg_idx operand is at start+1 (literal, no patching); the
                // four addresses follow at +5, +9, +13, +17.
                dispatchSites.Add(start + 5);
                dispatchSites.Add(start + 9);
                dispatchSites.Add(start + 13);
                dispatchSites.Add(start + 17);
            }
        }

        // 3b — final chain (full try/retry/trust over all clauses).
        EmitChain(emitter, Enumerable.Range(0, n).ToList(), clauseBodyPos, arity, dispatchSites);

        // 3b' — var-only chain (single-arg case, 2+ var-headed clauses).
        // Emitted right after the final chain so its layout position
        // (allocated immediately after finalChainPos in pass 1) matches.
        if (singleLevel && levels[0].VarIdxs.Count >= 2)
            EmitChain(emitter, levels[0].VarIdxs, clauseBodyPos, arity, dispatchSites);

        // 3c — per-level sub-dispatches.
        for (int li = 0; li < levels.Length; li++)
        {
            var lvl = levels[li];
            if (lvl.HasAtoms)
            {
                if (lvl.ArgIdx == 0)
                {
                    int site = emitter.Position + 1;
                    switchTableIdSites.Add(site);
                    emitter.EmitSwitchOnAtom(lvl.AtomTableId);
                }
                else
                {
                    int site = emitter.Position + 5;
                    switchTableIdSites.Add(site);
                    emitter.EmitSwitchOnAtomArg(lvl.ArgIdx, lvl.AtomTableId);
                }
            }
            if (lvl.HasInts)
            {
                if (lvl.ArgIdx == 0)
                {
                    int site = emitter.Position + 1;
                    switchTableIdSites.Add(site);
                    emitter.EmitSwitchOnInteger(lvl.IntTableId);
                }
                else
                {
                    int site = emitter.Position + 5;
                    switchTableIdSites.Add(site);
                    emitter.EmitSwitchOnIntegerArg(lvl.ArgIdx, lvl.IntTableId);
                }
            }
            if (lvl.ListSub != null)
                EmitSubSwitch(lvl.ListSub);
            else if (lvl.ListBucket.Count >= 2)
                EmitChain(emitter, lvl.ListBucket, clauseBodyPos, arity, dispatchSites);
            if (lvl.HasStructs)
            {
                if (lvl.ArgIdx == 0)
                {
                    int site = emitter.Position + 1;
                    switchTableIdSites.Add(site);
                    emitter.EmitSwitchOnStructure(lvl.StructTableId);
                }
                else
                {
                    int site = emitter.Position + 5;
                    switchTableIdSites.Add(site);
                    emitter.EmitSwitchOnStructureArg(lvl.ArgIdx, lvl.StructTableId);
                }
            }

            foreach (var (key, group) in lvl.AtomBuckets)
                if (group.Count >= 2)
                {
                    if (lvl.AtomGroupSub.TryGetValue(key, out var ss)) EmitSubSwitch(ss);
                    else EmitChain(emitter, group, clauseBodyPos, arity, dispatchSites);
                }
            foreach (var (key, group) in lvl.IntBuckets)
                if (group.Count >= 2)
                {
                    if (lvl.IntGroupSub.TryGetValue(key, out var ss)) EmitSubSwitch(ss);
                    else EmitChain(emitter, group, clauseBodyPos, arity, dispatchSites);
                }
            foreach (var (key, group) in lvl.StructBuckets)
                if (group.Count >= 2)
                {
                    if (lvl.StructGroupSub.TryGetValue(key, out var ss)) EmitSubSwitch(ss);
                    else EmitChain(emitter, group, clauseBodyPos, arity, dispatchSites);
                }
        }

        // 3d — clause bodies.
        for (int i = 0; i < n; i++)
        {
            if (emitDebugInfo) emitter.EmitMetaDbgInfo(i);
            // dynamic clauses run a check_visible
            // sentinel (born=0, died=MaxValue) — the persistent
            // buffer is rebuilt on every mutation so live born/died
            // values come from the rebuild itself. (The in-place
            // chain machinery patches real born/died without a
            // rebuild on the incremental paths.)
            if (isDynamic)
                emitter.EmitCheckVisible(born: 0L, died: long.MaxValue);
            int clauseStart = emitter.Position;
            emitter.AppendBytes(compiledClauses[i].Bytecode);
            foreach (var site in compiledClauses[i].CallSites)
                callSites.Add(new CallSite(
                    clauseStart + site.OpcodeOffset, site.CalleeFunctorId, site.IsExecute));
            debugStops.AddRange(ShiftDebugStops(compiledClauses[i], clauseStart));   // ADR-035
            debugFrames.AddRange(ClauseFrames(compiledClauses[i], clauseStart, i + 1));   // ADR-035
            MergeClauseDispatchSites(emitter, compiledClauses[i], clauseStart, dispatchSites);
        }

        return new CompiledPredicate(
            emitter.ToBytes(), functorId, arity, n,
            callSites, dispatchSites, switchTables, switchTableIdSites, position,
            clausePositions)
        {
            DebugStops = debugStops,   // ADR-035
            DebugFrames = debugFrames,   // ADR-035
        };
    }

    /// <summary>Emits a <c>try</c> / (zero or more <c>retry</c>) / <c>trust</c>
    /// sequence over <paramref name="clauseIndices"/>'s clause body offsets.
    /// Each instruction's address operand is registered as a dispatch site so
    /// the linker can later shift them from predicate-local to absolute. Single
    /// or empty buckets don't emit anything — their lookups jump directly to
    /// the clause body (or fall to the default chain).</summary>
    private static void EmitChain(
        BytecodeEmitter emitter,
        IReadOnlyList<int> clauseIndices,
        int[] clauseBodyPos,
        int arity,
        List<int> dispatchSites)
    {
        int count = clauseIndices.Count;
        if (count < 2) return;

        for (int i = 0; i < count; i++)
        {
            int target = clauseBodyPos[clauseIndices[i]];
            int opPos = emitter.Position;
            if (i == 0)
            {
                emitter.EmitTry(target, arity);
                dispatchSites.Add(opPos + 1);
            }
            else if (i == count - 1)
            {
                emitter.EmitTrust(target);
                dispatchSites.Add(opPos + 1);
            }
            else
            {
                emitter.EmitRetry(target);
                dispatchSites.Add(opPos + 1);
            }
        }
    }

    private static List<int> GetOrAdd(SortedDictionary<int, List<int>> map, int key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<int>();
            map[key] = list;
        }
        return list;
    }

    // ============================================================================
    // Extensible indexed dispatch for dynamic predicates
    // ============================================================================

    /// <summary>first-argument indexed compilation for
    /// dynamic predicates with chains that can be extended in place
    /// by <c>assertz</c> / <c>asserta</c> without re-linking the
    /// predicate. Differences from <see cref="CompileIndexed"/>:
    /// <list type="bullet">
    /// <item>Bucket chains use <c>try_me_else</c> / <c>retry_me_else</c>
    ///   (chain-walking via patchable <c>&lt;next&gt;</c> operands) instead
    ///   of the contiguous <c>try</c> / <c>retry</c> / <c>trust</c>
    ///   triplet — the incremental-assertz path can extend
    ///   any chain's tail by appending a new chunk at the end of the
    ///   buffer and patching the previous tail's operand.</item>
    /// <item>Each chain entry is <c>try_me_else &lt;next&gt; arity</c> /
    ///   <c>retry_me_else &lt;next&gt;</c> + <c>check_visible &lt;born&gt; &lt;died&gt;</c>
    ///   + <c>execute &lt;body_addr&gt;</c>; bodies live once and are
    ///   reached via execute so a clause that's in multiple chains
    ///   (its specific bucket + the var-fallthrough chain) shares
    ///   one body.</item>
    /// <item><c>enter_dynamic</c> at the predicate entry samples
    ///   <c>DbGeneration</c> into <c>CurrentViewGen</c> so each entry's
    ///   <c>check_visible</c> filters against a stable view.</item>
    /// </list>
    /// </summary>
    private static CompiledPredicate CompileIndexedDynamic(
        IReadOnlyList<CompiledClause> compiledClauses,
        ArgInfo[][] perArgInfo,
        List<int> indexableArgs,
        int functorId,
        int arity,
        Shumway.Compiler.Lexer.SourcePosition position,
        IReadOnlyList<Shumway.Compiler.Lexer.SourcePosition> clausePositions,
        int failStubAddr,
        bool emitDebugInfo = true)
    {
        int metaDbgInfoSize = emitDebugInfo ? MetaDbgInfoSize : 0;
        int n = compiledClauses.Count;
        int numLevels = indexableArgs.Count;

        // ----- Per-level bucket structure -----
        // Each level corresponds to one indexable arg. Level li's
        // candidate clauses are those whose args at ALL previous
        // indexable positions are Var (so dispatch reached this level
        // via the var-fallthrough cascade). Within candidates, bucket
        // by arg[indexableArgs[li]] classification, then merge var-
        // clauses-at-this-level into every concrete bucket.
        var levels = new LevelData[numLevels];
        for (int li = 0; li < numLevels; li++)
        {
            int argIdx = indexableArgs[li];
            var lvl = new LevelData { ArgIdx = argIdx };
            // Determine candidates: clauses with Var/Other at every
            // previous indexable arg.
            bool IsEligibleAtLevel(int i)
            {
                for (int prev = 0; prev < li; prev++)
                {
                    var k = perArgInfo[indexableArgs[prev]][i].Kind;
                    if (k != ArgKind.Var && k != ArgKind.Other) return false;
                }
                return true;
            }
            // Classify each candidate at this level.
            var varHere = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!IsEligibleAtLevel(i)) continue;
                var info = perArgInfo[argIdx][i];
                switch (info.Kind)
                {
                    case ArgKind.Var:
                    case ArgKind.Other:  varHere.Add(i); break;
                    case ArgKind.Atom:   GetOrAdd(lvl.AtomMap, info.Key).Add(i); break;
                    case ArgKind.Int:    GetOrAdd(lvl.IntMap, info.Key).Add(i); break;
                    case ArgKind.Struct: GetOrAdd(lvl.StructMap, info.Key).Add(i); break;
                    case ArgKind.List:   lvl.ListClauses.Add(i); break;
                }
            }
            lvl.VarClauses = varHere;
            // Merge var-clauses-at-this-level into every concrete
            // bucket so a query with a concrete value still sees the
            // var-arg matches at this level.
            List<int> Merge(List<int> bucket)
            {
                var seen = new HashSet<int>(bucket);
                foreach (int v in varHere) seen.Add(v);
                var merged = seen.ToList();
                merged.Sort();
                return merged;
            }
            lvl.AtomMerged   = lvl.AtomMap.ToDictionary(kv => kv.Key, kv => Merge(kv.Value));
            lvl.IntMerged    = lvl.IntMap.ToDictionary(kv => kv.Key, kv => Merge(kv.Value));
            lvl.StructMerged = lvl.StructMap.ToDictionary(kv => kv.Key, kv => Merge(kv.Value));
            // The list bucket: list-arg clauses ∪ var-arg clauses
            // (at this level).
            lvl.ListMerged = (lvl.ListClauses.Count > 0 || varHere.Count > 0)
                ? Merge(lvl.ListClauses) : new List<int>();
            levels[li] = lvl;
        }

        // The final var-fallthrough chain enumerates every clause —
        // reached when var passes through every level.
        var varChain = Enumerable.Range(0, n).ToList();

        // ----- Layout pass -----
        const int EnterDynamicSize = 1;
        const int ChainHeadSize = 9 + 17 + 5;
        const int ChainNonHeadSize = 5 + 17 + 5;
        int ChainSize(int count) =>
            count == 0 ? 0 : ChainHeadSize + (count - 1) * ChainNonHeadSize;
        int TopSwitchSize(int argIdx) => argIdx == 0 ? 17 : 21;
        int SubSwitchSize(int argIdx) => argIdx == 0 ? 5 : 9;

        int pos = EnterDynamicSize;
        // Top-level switch per level (chained head-to-tail via var-
        // fallthrough). Level 0 uses switch_on_term (arg 0 implied);
        // higher levels use switch_on_arg <arg_idx, …>.
        for (int li = 0; li < numLevels; li++)
        {
            levels[li].TopSwitchPos = pos;
            pos += TopSwitchSize(levels[li].ArgIdx);
        }
        // Per-level sub-dispatches (switch_on_atom / _integer /
        // _structure).
        for (int li = 0; li < numLevels; li++)
        {
            var lvl = levels[li];
            int sub = SubSwitchSize(lvl.ArgIdx);
            if (lvl.AtomMerged.Count > 0)   { lvl.SubAtomPos = pos;   pos += sub; }
            if (lvl.IntMerged.Count > 0)    { lvl.SubIntPos = pos;    pos += sub; }
            if (lvl.StructMerged.Count > 0) { lvl.SubStructPos = pos; pos += sub; }
        }
        // Per-level bucket chains.
        for (int li = 0; li < numLevels; li++)
        {
            var lvl = levels[li];
            foreach (var (key, clauses) in lvl.AtomMerged)
            {
                lvl.AtomChainHeads[key] = pos;
                pos += ChainSize(clauses.Count);
            }
            foreach (var (key, clauses) in lvl.IntMerged)
            {
                lvl.IntChainHeads[key] = pos;
                pos += ChainSize(clauses.Count);
            }
            foreach (var (key, clauses) in lvl.StructMerged)
            {
                lvl.StructChainHeads[key] = pos;
                pos += ChainSize(clauses.Count);
            }
            if (lvl.ListMerged.Count > 0)
            {
                lvl.ListChainHead = pos;
                pos += ChainSize(lvl.ListMerged.Count);
            }
        }
        // Final var-fallthrough chain.
        int varChainHead = pos;
        pos += ChainSize(varChain.Count);
        // Clause bodies live once.
        int[] bodyAddr = new int[n];
        for (int i = 0; i < n; i++)
        {
            bodyAddr[i] = pos;
            pos += metaDbgInfoSize;
            pos += compiledClauses[i].Bytecode.Length;
        }

        // ----- Wire each level's var-fallthrough -----
        // Level li's var label → level li+1's top switch (cascading
        // down), or the final var chain if last level.
        for (int li = 0; li < numLevels; li++)
            levels[li].VarLbl = li + 1 < numLevels
                ? levels[li + 1].TopSwitchPos
                : varChainHead;

        // For each level, compute the const cascade label, list label,
        // and struct label — same logic as the single-arg
        // case, just per-level.
        for (int li = 0; li < numLevels; li++)
        {
            var lvl = levels[li];
            lvl.ConstLbl =
                lvl.AtomMerged.Count > 0   ? lvl.SubAtomPos
              : lvl.IntMerged.Count > 0    ? lvl.SubIntPos
              : lvl.StructMerged.Count > 0 ? lvl.SubStructPos
              : lvl.VarLbl;
            lvl.ListLbl = lvl.ListChainHead >= 0 ? lvl.ListChainHead : lvl.VarLbl;
            lvl.StructLbl = lvl.StructMerged.Count > 0 ? lvl.SubStructPos : lvl.VarLbl;
        }

        // ----- Build switch tables -----
        var switchTables = new List<SwitchTable>();
        for (int li = 0; li < numLevels; li++)
        {
            var lvl = levels[li];
            if (lvl.AtomMerged.Count > 0)
            {
                lvl.AtomTableId = switchTables.Count;
                int dft = lvl.IntMerged.Count > 0   ? lvl.SubIntPos
                        : lvl.StructMerged.Count > 0 ? lvl.SubStructPos
                        : lvl.VarLbl;
                switchTables.Add(new SwitchTable(
                    lvl.AtomChainHeads.Keys.ToArray(),
                    lvl.AtomChainHeads.Values.ToArray(), dft));
            }
            if (lvl.IntMerged.Count > 0)
            {
                lvl.IntTableId = switchTables.Count;
                int dft = lvl.StructMerged.Count > 0 ? lvl.SubStructPos : lvl.VarLbl;
                switchTables.Add(new SwitchTable(
                    lvl.IntChainHeads.Keys.ToArray(),
                    lvl.IntChainHeads.Values.ToArray(), dft));
            }
            if (lvl.StructMerged.Count > 0)
            {
                lvl.StructTableId = switchTables.Count;
                switchTables.Add(new SwitchTable(
                    lvl.StructChainHeads.Keys.ToArray(),
                    lvl.StructChainHeads.Values.ToArray(), lvl.VarLbl));
            }
        }

        // ----- Emit pass -----
        var emitter = new BytecodeEmitter();
        var callSites = new List<CallSite>();
        var dispatchSites = new List<int>();
        var debugStops = new List<DebugStop>();   // ADR-035
        var debugFrames = new List<DebugClauseFrame>();   // ADR-035
        var switchTableIdSites = new List<int>();

        emitter.EmitEnterDynamic();

        // Top-level switches.
        for (int li = 0; li < numLevels; li++)
        {
            var lvl = levels[li];
            int start = emitter.Position;
            if (lvl.ArgIdx == 0)
            {
                emitter.EmitSwitchOnTerm(lvl.VarLbl, lvl.ConstLbl, lvl.ListLbl, lvl.StructLbl);
                dispatchSites.Add(start + 1);
                dispatchSites.Add(start + 5);
                dispatchSites.Add(start + 9);
                dispatchSites.Add(start + 13);
            }
            else
            {
                emitter.EmitSwitchOnArg(
                    lvl.ArgIdx, lvl.VarLbl, lvl.ConstLbl, lvl.ListLbl, lvl.StructLbl);
                // switch_on_arg: opcode (1) + arg_idx (4) + 4 addresses (4×4).
                dispatchSites.Add(start + 5);
                dispatchSites.Add(start + 9);
                dispatchSites.Add(start + 13);
                dispatchSites.Add(start + 17);
            }
        }
        // Per-level sub-dispatches.
        for (int li = 0; li < numLevels; li++)
        {
            var lvl = levels[li];
            if (lvl.AtomMerged.Count > 0)
            {
                int siteOffset = lvl.ArgIdx == 0 ? 1 : 5;
                int site = emitter.Position + siteOffset;
                switchTableIdSites.Add(site);
                if (lvl.ArgIdx == 0) emitter.EmitSwitchOnAtom(lvl.AtomTableId);
                else                 emitter.EmitSwitchOnAtomArg(lvl.ArgIdx, lvl.AtomTableId);
            }
            if (lvl.IntMerged.Count > 0)
            {
                int siteOffset = lvl.ArgIdx == 0 ? 1 : 5;
                int site = emitter.Position + siteOffset;
                switchTableIdSites.Add(site);
                if (lvl.ArgIdx == 0) emitter.EmitSwitchOnInteger(lvl.IntTableId);
                else                 emitter.EmitSwitchOnIntegerArg(lvl.ArgIdx, lvl.IntTableId);
            }
            if (lvl.StructMerged.Count > 0)
            {
                int siteOffset = lvl.ArgIdx == 0 ? 1 : 5;
                int site = emitter.Position + siteOffset;
                switchTableIdSites.Add(site);
                if (lvl.ArgIdx == 0) emitter.EmitSwitchOnStructure(lvl.StructTableId);
                else                 emitter.EmitSwitchOnStructureArg(lvl.ArgIdx, lvl.StructTableId);
            }
        }

        void EmitChain(IReadOnlyList<int> clauseIndices)
        {
            for (int i = 0; i < clauseIndices.Count; i++)
            {
                int idx = clauseIndices[i];
                int entryStart = emitter.Position;
                bool isLast = i == clauseIndices.Count - 1;
                int thisEntrySize = i == 0 ? ChainHeadSize : ChainNonHeadSize;
                int nextAddr = isLast ? failStubAddr : (entryStart + thisEntrySize);
                if (i == 0)
                {
                    emitter.EmitTryMeElse(nextAddr, arity);
                    if (!isLast) dispatchSites.Add(entryStart + 1);
                }
                else
                {
                    emitter.EmitRetryMeElse(nextAddr);
                    if (!isLast) dispatchSites.Add(entryStart + 1);
                }
                emitter.EmitCheckVisible(born: 0L, died: long.MaxValue);
                int execOpPos = emitter.Position;
                emitter.EmitExecute(bodyAddr[idx]);
                dispatchSites.Add(execOpPos + 1);
            }
        }

        // Per-level bucket chains.
        for (int li = 0; li < numLevels; li++)
        {
            var lvl = levels[li];
            foreach (var (_, clauses) in lvl.AtomMerged)   EmitChain(clauses);
            foreach (var (_, clauses) in lvl.IntMerged)    EmitChain(clauses);
            foreach (var (_, clauses) in lvl.StructMerged) EmitChain(clauses);
            if (lvl.ListChainHead >= 0) EmitChain(lvl.ListMerged);
        }
        // Final var-fallthrough chain.
        EmitChain(varChain);

        // Bodies.
        for (int i = 0; i < n; i++)
        {
            if (emitDebugInfo) emitter.EmitMetaDbgInfo(i);
            int clauseStart = emitter.Position;
            emitter.AppendBytes(compiledClauses[i].Bytecode);
            foreach (var site in compiledClauses[i].CallSites)
                callSites.Add(new CallSite(
                    clauseStart + site.OpcodeOffset, site.CalleeFunctorId, site.IsExecute));
            debugStops.AddRange(ShiftDebugStops(compiledClauses[i], clauseStart));   // ADR-035
            debugFrames.AddRange(ClauseFrames(compiledClauses[i], clauseStart, i + 1));   // ADR-035
            MergeClauseDispatchSites(emitter, compiledClauses[i], clauseStart, dispatchSites);
        }

        return new CompiledPredicate(
            emitter.ToBytes(), functorId, arity, n,
            callSites, dispatchSites, switchTables, switchTableIdSites, position,
            clausePositions)
        {
            DebugStops = debugStops,   // ADR-035
            DebugFrames = debugFrames,   // ADR-035
        };
    }

    /// <summary>Per-level bucket / chain bookkeeping for the multi-arg
    /// extensible-indexed compilation.</summary>
    private sealed class LevelData
    {
        public int ArgIdx;
        public List<int> VarClauses = new();
        public SortedDictionary<int, List<int>> AtomMap = new();
        public SortedDictionary<int, List<int>> IntMap = new();
        public SortedDictionary<int, List<int>> StructMap = new();
        public List<int> ListClauses = new();
        public Dictionary<int, List<int>> AtomMerged = new();
        public Dictionary<int, List<int>> IntMerged = new();
        public Dictionary<int, List<int>> StructMerged = new();
        public List<int> ListMerged = new();
        public int TopSwitchPos;
        public int SubAtomPos = -1, SubIntPos = -1, SubStructPos = -1;
        public Dictionary<int, int> AtomChainHeads = new();
        public Dictionary<int, int> IntChainHeads = new();
        public Dictionary<int, int> StructChainHeads = new();
        public int ListChainHead = -1;
        public int VarLbl, ConstLbl, ListLbl, StructLbl;
        public int AtomTableId = -1, IntTableId = -1, StructTableId = -1;
    }
}

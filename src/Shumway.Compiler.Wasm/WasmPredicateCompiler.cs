using Shumway.Compiler.Wam;
using Shumway.Core;
using WebAssembly;
using WebAssembly.Instructions;
using SwitchTable = Shumway.Core.SwitchTable;
using Tag = Shumway.Core.Tag;

namespace Shumway.Compiler.Wasm;

/// <summary>WAM bytecode to a WebAssembly module (the plan's phase 1, first
/// slice: head matching, integer arithmetic, environment frames, choice
/// points, calls).
///
/// <para>The translation is against the ENGINE's own state: the module reads
/// its bases from the mailbox and manipulates the heap, stack, registers and
/// binding trail exactly as the interpreter would -- same frame layout, same
/// choice-point words, same trail rule -- so control can move between tiers
/// at any instruction boundary. Anything the compiled code cannot do (an
/// attributed variable, arithmetic past the small-integer lane, a full
/// trail) is a <see cref="WasmVerdict.Deopt"/>: sync the scalars, name the
/// bytecode address, and let the interpreter take over mid-clause.</para>
///
/// <para>Control flow is the classic dispatcher loop: every jump target gets
/// a cursor id, a <c>br_table</c> at the top routes to the current cursor's
/// block, and a block ends by setting the cursor and branching back (or
/// returning a verdict). The cursor is also the RE-ENTRY vocabulary: resume
/// markers and choice-point BPs name cursors, so a call's return and a
/// backtrack land on the same dispatch.</para></summary>
public static class WasmPredicateCompiler
{
    public static WasmEntry Compile(CompiledPredicate predicate, IWasmCompileEnv env,
                                    bool shared = false,
                                    IReadOnlyList<double>? floatLiterals = null)
    {
        var g = CompileGroup(
            new[] { new WasmGroupMember(predicate, 0, floatLiterals) }, env, shared);
        return new WasmEntry(g.Module, predicate.FunctorId, predicate.Arity,
                             g.CursorByAddress, g.RegisterDemand);
    }

    /// <summary>Compiles a GROUP of predicates into one module over a unified
    /// pc space: each member's bias (its linked base in the engine) offsets
    /// its bytecode addresses, so pcs never collide, a deopt pc needs no
    /// translation, and a cross-member call is an internal dispatch jump --
    /// the self-tail mechanism generalised. Cursors are global to the module;
    /// markers carry (fid, global cursor).</summary>
    /// <param name="moduleId">The id the installing world gave this module.
    /// BAKED into the code, not read from the mailbox: after a hop the
    /// mailbox is still the chain's, and a module that took its identity
    /// from there would dispatch another module's cursors as its own.</param>
    public static WasmGroupEntry CompileGroup(IReadOnlyList<WasmGroupMember> members,
                                              IWasmCompileEnv env, bool shared = false,
                                              int moduleId = 0)
    {
        var c = new Compilation(members, env, moduleId);
        c.RejectDispatchOnlyPredicates();
        c.Decode();
        c.RejectIfCrossingsDominate();
        c.AssignCursors();
        byte[] bytes = c.Emit();
        if (shared) bytes = WasmSharedMemory.Patch(bytes);
        var entryCursors = new Dictionary<int, int>(members.Count);
        foreach (var m in members)
            entryCursors[m.Predicate.FunctorId] = c.CursorByAddress[m.Bias];
        return new WasmGroupEntry(bytes, entryCursors, c.CursorByAddress,
                                  c.RegisterDemand, c.CallSites, moduleId);
    }

    /// <summary>Diagnostic: emit a dispatch counter + loop breaker into the
    /// dispatcher (see the guard at the loop top). Off in production.</summary>
    public static bool DebugLoopGuard;

    /// <summary>Emit the meta-call's guard stamps (which guard declined, and
    /// the functor id it saw) into DiagA/DiagB. OFF: unlike the host-side
    /// tallies, this one puts real instructions in every compiled module, so
    /// it cannot ride on SHUMWAY_DIAG -- a stock module would carry a store
    /// per guard. Turned on by hand when a decline has to be explained.
    ///
    /// <para>It earned its keep once already: the inline meta-call declined
    /// every time and the counters said so, but only these stamps said WHY --
    /// the goal arrives wrapped in '$mqual'(Module, Goal), so the functor the
    /// module reads is the wrapper's, not the goal's.</para></summary>
    public static bool DebugMetaGuards
#if SHUMWAY_DIAG
        = true
#endif
        ;

    /// <summary>What a stamp means, kept beside the codes so the two
    /// cannot drift. 1-16 are the meta-call guards; 17 up are the deopt
    /// reasons every other step-aside site names. A number in a histogram
    /// says the module came out; only the name says whether that is a limit
    /// of the design, a table the host never staged, or work that belongs
    /// to the engine.</summary>
    public static string DeoptReasonName(int code) => code switch
    {
        // Every deopt site stamps a reason or declares that a guard
        // already did, so nothing this compiler emits reads zero. A zero
        // therefore means the MODULE has no stamps at all: it was compiled
        // with DebugMetaGuards off, which is what a bundle baked at build
        // time by a non-diag build looks like.
        0 => "no stamp: a module compiled without stamps (a non-diag bake)",
        17 => "the host set a flag (a wakeup, an interrupt, a cancellation) "
              + "and the module came out at the next boundary",
        18 => "the callee is a builtin the module cannot request directly",
        19 => "the heap reached its watermark: the engine must collect",
        20 => "a packed string reached a dispatch the module hands back",
        21 => "a frame or choice point would cross the stack limit",
        22 => "the binding trail is full",
        23 => "a restore would have to unwind the extra trail",
        24 => "binding an attributed variable: the wakeup is the host's",
        25 => "a general unification only the engine's unifier can do",
        26 => "arithmetic: a shape or an error the evaluator hands to the host",
        1 => "no call-marker table staged",
        2 => "goal is neither a compound nor an atom",
        4 => "no module covers the goal's functor",
        5 => "goal arity is zero, or wider than the module takes",
        6 => "$mqual module is not a bound atom",
        7 => "$mqual goal is not a compound",
        8 => "no meta cache staged",
        9 => "meta cache has no row for this (module, goal)",
        10 => "meta cache probe found no row in a full pass",
        11 => "resolved arity disagrees with the goal's",
        12 => "the carried cut barrier is not an integer cell",
        13 => "no atom-marker table staged",
        14 => "atom id past the marker table",
        15 => "no module covers this zero-arity goal",
        16 => "setup_call_cleanup handlers are live, so the cut declines",
        _ => "unnamed",
    };

    // ---- locals (after the two i32 params: 0 mailbox, 1 entry cursor) ----
    private const uint LCur = 2;      // current cursor
    private const uint LHeapB = 3;    // byte base of the heap
    private const uint LStackB = 4;
    private const uint LRegsB = 5;
    private const uint LTrailB = 6;
    private const uint LH = 7;        // heap top (cell index)
    private const uint LTR = 8;       // binding trail top
    private const uint LE = 9;        // environment frame
    private const uint LB = 10;       // choice point
    private const uint LHB = 11;      // heap backtrack boundary
    private const uint LST = 12;      // stack top
    private const uint LCP = 13;      // continuation
    private const uint LT0 = 14;      // i32 scratch
    private const uint LT1 = 15;
    private const uint LT2 = 16;
    private const uint LDa = 17;      // deref: the last REF's home address
    private const uint LC0 = 18;      // i64 scratch (a cell)
    private const uint LC1 = 19;
    private const uint LC2 = 20;
    private const uint LMode = 21;    // i32: unify write mode
    private const uint LS = 22;       // i32: the unify pointer
    // i64 tallies, after the AEval bank (see EngineLocals): goals dispatched
    // and heap cells claimed, spilled to the mailbox with the scalars.
    private const uint LGoals = 31;
    private const uint LCells = 32;

    private const long RawIntTag = (long)Tag.RawInt << Cell.TagShift;

    private sealed record Instr(int Pc, Opcode Op, int Size, int Section)
    {
        public int I0, I1, I2, I3, I4;
    }

    private sealed class Compilation(IReadOnlyList<WasmGroupMember> members,
                                     IWasmCompileEnv env, int moduleId)
    {
        private readonly IReadOnlyList<WasmGroupMember> _members = members;
        private readonly IWasmCompileEnv _env = env;
        private readonly int _moduleId = moduleId;
        private readonly List<Instr> _instrs = new();
        private readonly Dictionary<int, int> _byPc = new();
        private readonly SortedSet<int> _leaders = new();
        private readonly Dictionary<int, int> _cursorByAddr = new();
        private readonly Dictionary<int, int> _callee = new();   // call-site pc -> functor
        // fid -> entry address (the member's bias): the in-group call map.
        private readonly Dictionary<int, int> _entryByFid = new();
        // (callerFid, calleeFid) -> call sites between them, for the coupling
        // report. Filled by the cursor pass, which walks every instruction
        // anyway, so it costs nothing at run time and nothing extra to compile.
        private readonly Dictionary<(int, int), int> _callSites = new();
        public IReadOnlyDictionary<(int, int), int> CallSites => _callSites;

        private WasmGroupMember Sec(Instr ins) => _members[ins.Section];
        private int Bias(Instr ins) => _members[ins.Section].Bias;
        private int SelfFid(Instr ins) => _members[ins.Section].Predicate.FunctorId;

        public IReadOnlyDictionary<int, int> CursorByAddress => _cursorByAddr;
        private int _failCase;      // pseudo-cursor: backtrack (not re-enterable)
        private int _proceedCase;   // pseudo-cursor: full proceed resolution
        private int _caseCount;     // per PARTITION while its body is emitted

        // Partitions of the cursor space, cut at member boundaries. One
        // function per partition: a single function holding the whole group
        // is over the JIT cliff (both RyuJIT and Liftoff) far below wasm's
        // validation limits — a 643k-instruction body compiles for MINUTES
        // where 634k takes seconds. Cursors are contiguous per member
        // (leaders are addresses, members' address ranges are disjoint), so
        // a partition is a cursor range [Lo, Hi).
        private readonly List<(int Lo, int Hi)> _parts = new();
        private (int Lo, int Hi) _curPart;

        /// <summary>Partition budget in DECODED WAM instructions (~40 wasm
        /// instructions each): ~100k emitted per function, far under the
        /// ~640k cliff. A SAFETY VALVE, and measured as one: over the
        /// prelude plus clpfd, no predicate compiled alone is ever cut (the
        /// largest takes one function), while the whole program as one group
        /// takes 7. It bites for the batch mode and for a generated fact
        /// table, which is one predicate that can cross the budget by
        /// itself. See PartitionBudgetTests.</summary>
        private const int PartitionBudgetWamInstrs = 2500;

        private int UnifierIndex => _parts.Count + 2;   // 0 run, 1..K parts, K+1 resolver

        // ------------------------------------------------------------------
        // Decode + census
        // ------------------------------------------------------------------

        public void Decode()
        {
            for (int sec = 0; sec < _members.Count; sec++)
            {
                var m = _members[sec];
                _entryByFid[m.Predicate.FunctorId] = m.Bias;
                foreach (var site in m.Predicate.CallSites)
                    _callee[m.Bias + site.OpcodeOffset] = site.CalleeFunctorId;
                DecodeSection(sec);
            }
        }

        // Instruction pcs are BIASED (linked-absolute); operand addresses
        // stay predicate-local and are biased at their use sites.
        private void DecodeSection(int sec)
        {
            var member = _members[sec];
            byte[] code = member.Predicate.Bytecode;
            int bias = member.Bias;
            int pc = 0;
            while (pc < code.Length)
            {
                var op = (Opcode)code[pc];
                // Meta carries compile-time metadata (DbgInfo) and runs as a
                // no-op; its size comes from its sub-opcode, not the table.
                if (op == Opcode.Meta)
                {
                    if (code[pc + 1] != 0)
                        throw new WasmCompileException($"meta sub-opcode {code[pc + 1]} at {pc}");
                    var meta = new Instr(bias + pc, op, 6, sec);
                    _byPc[bias + pc] = _instrs.Count;
                    _instrs.Add(meta);
                    pc += 6;
                    continue;
                }
                var info = OpcodeTable.Get(code[pc]);
                if (!info.IsDefined || info.Size <= 0)
                    throw new WasmCompileException($"undecodable opcode 0x{code[pc]:X2} at {pc}");
                var ins = new Instr(bias + pc, op, info.Size, sec);
                if (info.Size >= 5) ins.I0 = BytecodeIO.ReadInt32(code, pc + 1);
                if (info.Size >= 9) ins.I1 = BytecodeIO.ReadInt32(code, pc + 5);
                if (info.Size >= 13) ins.I2 = BytecodeIO.ReadInt32(code, pc + 9);
                if (info.Size >= 17) ins.I3 = BytecodeIO.ReadInt32(code, pc + 13);
                if (info.Size >= 21) ins.I4 = BytecodeIO.ReadInt32(code, pc + 17);
                _byPc[bias + pc] = _instrs.Count;
                _instrs.Add(ins);
                Census(ins);
                pc += info.Size;
            }
        }

        // Per-member (per-section) cost census, filled by Census during
        // Decode. sureExits are opcodes that ALWAYS cross to the host when
        // run (a C# builtin with no inline form -- catch_begin/end, put_attr,
        // most of the registry); compilable are the ones that run inside the
        // module. A call to another predicate is neither: its cost belongs to
        // the callee, which promotes or not on its own (ADR: static promotion
        // census, 2026-09).
        private int[]? _sureExits;
        private int[]? _compilable;

        /// <summary>Refuses to promote a member whose guaranteed host
        /// crossings are not paid for by the work that runs in the module.
        ///
        /// <para>The shape this exists for is catch/3 around a call
        /// (deep3/perftest1.pl): $catch_begin and $catch_end are two host
        /// crossings per execution, the body is one `is`, and the recursion
        /// is a call whose cost is the callee's. Promoted, it ran 24x SLOWER
        /// than Tier-0 in a desktop measurement -- 40,000 crossings for
        /// 20,000 levels. The census is a by-product of the decode pass that
        /// already visits every opcode, so it costs nothing at run time,
        /// which is the whole reason it is static and not measured: measuring
        /// would tax every module that runs WELL.</para>
        ///
        /// <para>The bar is deliberately low -- reject only when crossings
        /// EQUAL OR OUTNUMBER the compilable work -- so it catches the clear
        /// losers (deep3) without second-guessing predicates that gain today.
        /// A member with no crossings at all is never in question.</para>
        /// </summary>
        /// <summary>Some predicates are not what their clauses say they are.
        /// <c>':'/2</c> and <c>'$mqual'/2</c> carry a module and a goal, and
        /// the INTERPRETER intercepts them inside its own dispatch: their
        /// clauses exist to have something to name, and running them as
        /// written does not do what calling them does.
        ///
        /// <para>So they cannot be compiled. On the tier <c>':'/2</c> ran
        /// its own body and a qualified goal came back as an
        /// existence_error on the MODULE ATOM -- not even the goal -- while
        /// Tier 0 answered. Refusing them costs nothing: a call to one takes
        /// the ordinary path for an uncompiled callee and lands on its
        /// bytecode, which is where its meaning lives.</para></summary>
        public void RejectDispatchOnlyPredicates()
        {
            foreach (var m in _members)
            {
                int fid = m.Predicate.FunctorId;
                if (fid != _env.ColonFunctorId && fid != _env.MqualFunctorId)
                    continue;
                var (aid, ar) = Shumway.Core.FunctorTable.Lookup(fid);
                throw new WasmCompileException(
                    $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar} is "
                    + "dispatched by the interpreter, not run as written");
            }
        }

        public void RejectIfCrossingsDominate()
        {
            if (_sureExits is null) return;
            for (int sec = 0; sec < _members.Count; sec++)
            {
                int exits = _sureExits[sec];
                // A single crossing never disqualifies. A one-line wrapper
                // over a C# builtin (clpfd_iv/3 and friends) crosses once
                // whether it is promoted or not -- promoted it exits mid-
                // chain, refused it closes and reopens the chain -- so
                // refusing it buys nothing and measured WORSE on queens_fd
                // (chains 1,459 -> 2,669 for 174 fewer exits). The damage
                // this census exists for starts at TWO crossings per pass,
                // which is what catch/3 costs.
                if (exits < 2) continue;
                // What one host crossing costs, measured in compilable ops
                // it takes to pay for it. The honest number is in the
                // hundreds (a crossing is microseconds, an op's wasm gain is
                // nanoseconds), but the census counts TEXT, not executions:
                // a hot loop re-runs its compilable ops thousands of times
                // per crossing elsewhere (clpfd does exactly that and wins
                // 5.6x WITH crossings in its text). So the weight stays low
                // on purpose: catch only the bodies whose text is dominated
                // by crossings -- deep3 and its catch helper -- and leave
                // anything with real work alone.
                const int CrossingWeight = 8;
                if (exits * CrossingWeight >= _compilable![sec])
                {
                    var (aid, ar) = Shumway.Core.FunctorTable.Lookup(
                        _members[sec].Predicate.FunctorId);
                    string name = Shumway.Core.AtomTable.GetById(aid)?.Name ?? "?";
                    throw new WasmCompileException(
                        $"{name}/{ar}: {exits} host crossings against "
                        + $"{_compilable[sec]} compilable ops -- promoting it "
                        + "would cross more than it runs");
                }
            }
        }

        /// <summary>Whether a builtin has a form the emitter runs INSIDE the
        /// module rather than exiting to the host. The one place that answer
        /// lives: EmitCall dispatches on exactly these, and the census reads
        /// the same list, so the two cannot drift on which builtins stay in
        /// wasm.</summary>
        /// <summary>Classifies one instruction into the cost census. Called
        /// from Census, so it rides the decode pass. Default is compilable:
        /// the exits and the neutrals are enumerated, everything else runs in
        /// the module.</summary>
        private void CostCensus(Instr ins)
        {
            _sureExits ??= new int[_members.Count];
            _compilable ??= new int[_members.Count];
            int sec = ins.Section;
            switch (ins.Op)
            {
                // A call to another predicate: its work is the callee's, and
                // it reaches the callee by a local jump or an in-wasm hop, so
                // it is neither a crossing nor this member's compilable work.
                //
                // EXCEPT a call to this predicate's own '$catchgoal_N' helper.
                // catch/3 in a body is rewritten to that call (MetaTransform.
                // RewriteCatch), and the helper's $catch_begin/$catch_end are
                // the CALLER's catch: two host crossings every time this body
                // runs, owned here even though the opcodes sit in the helper.
                // Counting them only in the helper let deep3 promote with
                // "zero" crossings and bounce wasm-host-wasm on every level.
                // Matching by name is the established contract for these
                // helpers (BundleWriter does the same).
                case Opcode.Call:
                case Opcode.Execute:
                case Opcode.DeallocateExecute:
                case Opcode.DeallocateProceed:
                case Opcode.Proceed:
                case Opcode.CutProceed:
                case Opcode.CutDeallocateProceed:
                // Frame and clause control: bookkeeping, not work that a
                // crossing has to be weighed against.
                case Opcode.Allocate:
                case Opcode.AllocateGetLevel:
                case Opcode.Deallocate:
                case Opcode.TryMeElse:
                case Opcode.RetryMeElse:
                case Opcode.TrustMe:
                case Opcode.Try:
                case Opcode.Retry:
                case Opcode.Trust:
                case Opcode.Jump:
                case Opcode.Meta:
                    if (ins.Op is Opcode.Call or Opcode.Execute or Opcode.DeallocateExecute
                        && _callee.TryGetValue(ins.Pc, out int callee))
                    {
                        var (aid, _) = Shumway.Core.FunctorTable.Lookup(callee);
                        string nm = Shumway.Core.AtomTable.GetById(aid)?.Name ?? "";
                        if (nm.Contains("$catchgoal_")) _sureExits[sec] += 2;
                    }
                    return;
                case Opcode.CallBuiltin:
                case Opcode.ExecuteBuiltin:
                    if (BuiltinHasInlineForm(ins.I0)) _compilable[sec]++;
                    else _sureExits[sec]++;      // a guaranteed host crossing
                    return;
                default:
                    _compilable[sec]++;          // runs in the module
                    return;
            }
        }

        private bool BuiltinHasInlineForm(int builtinId)
            => _env.IsInlineUnify(builtinId)
            || _env.IsInlineCompare(builtinId, out _)
            || (_env.TryGetInlineTypeTest(builtinId, out var t) && t != WasmTypeTest.None)
            || _env.IsInlineGetAttr(builtinId)
            || _env.IsInlineDomSame(builtinId)
            || _env.IsInlineDomEmpty(builtinId)
            || _env.IsInlineDomContains(builtinId)
            || _env.IsInlineDomDel(builtinId)
            || _env.IsInlineDomSingleton(builtinId)
            || _env.IsInlineMetaCall(builtinId)
            || _env.IsInlineBarrierCall(builtinId);

        /// <summary>The translatable set, and the reason when it is not.
        /// Everything else in the 57-opcode universe rejects the predicate:
        /// it stays on the tier it was on.</summary>
        private void Census(Instr ins)
        {
            CostCensus(ins);

            switch (ins.Op)
            {
                case Opcode.SwitchOnTerm:
                case Opcode.SwitchOnInteger:
                case Opcode.SwitchOnAtom:
                case Opcode.SwitchOnArg:
                case Opcode.SwitchOnIntegerArg:
                case Opcode.SwitchOnAtomArg:
                case Opcode.SwitchOnStructure:
                case Opcode.SwitchOnStructureArg:
                case Opcode.SwitchOnAtomSub:
                case Opcode.SwitchOnIntegerSub:
                case Opcode.SwitchOnStructureSub:
                case Opcode.Try:
                case Opcode.Retry:
                case Opcode.Trust:
                case Opcode.Allocate:
                case Opcode.Deallocate:
                case Opcode.DeallocateProceed:
                case Opcode.Proceed:
                case Opcode.GetVariableY:
                case Opcode.GetVariableX:
                case Opcode.GetValueY:
                case Opcode.GetValueX:
                case Opcode.PutValueY:
                case Opcode.PutVariableY:
                case Opcode.PutValueX:
                case Opcode.PutVariableX:
                case Opcode.PutInteger:
                case Opcode.PutAtom:
                case Opcode.PutNil:
                case Opcode.GetInteger:
                case Opcode.GetAtom:
                case Opcode.GetNil:
                case Opcode.Call:
                case Opcode.Execute:
                case Opcode.GetStructure:
                case Opcode.GetList:
                case Opcode.GetListA1:
                case Opcode.GetListA2:
                case Opcode.PutStructure:
                case Opcode.PutList:
                case Opcode.UnifyVariableX:
                case Opcode.UnifyVariableY:
                case Opcode.UnifyValueX:
                case Opcode.UnifyValueY:
                case Opcode.UnifyAtom:
                case Opcode.UnifyConstant:
                case Opcode.UnifyInteger:
                case Opcode.UnifyNil:
                case Opcode.UnifyVoid:
                case Opcode.UnifyStructure:
                case Opcode.UnifyList:
                case Opcode.GetConstantA1:
                case Opcode.GetConstantA2:
                case Opcode.PutConstantA1:
                case Opcode.PutConstantA2:
                case Opcode.DeallocateExecute:
                case Opcode.NeckCut:
                case Opcode.Cut:
                case Opcode.GetLevel:
                case Opcode.GetLevelB:
                case Opcode.AllocateGetLevel:
                case Opcode.CutProceed:
                case Opcode.CutDeallocateProceed:
                case Opcode.TryMeElse:
                case Opcode.RetryMeElse:
                case Opcode.TrustMe:
                case Opcode.Jump:
                case Opcode.CallBuiltin:
                case Opcode.ExecuteBuiltin:
                    return;
                case Opcode.GetFloat:
                case Opcode.PutFloat:
                    if (Sec(ins).FloatLiterals is null)
                        throw new WasmCompileException($"{ins.Op} without a float pool at {ins.Pc}");
                    return;
                case Opcode.AIntCmp:
                case Opcode.Meta:
                    return;
                case Opcode.PutStructureR:
                case Opcode.PutListR:
                    return;     // the region is validated during emission
                case Opcode.AEvalPush:
                case Opcode.AEvalBin:
                case Opcode.AEvalUn:
                case Opcode.AEvalCmp:
                    return;     // unsupported kinds/ops become deopts, not rejects
                case Opcode.AEvalIs:
                    if (ins.I0 is < 3 or > 6)
                        throw new WasmCompileException($"a_eval_is kind {ins.I0} at {ins.Pc}");
                    return;
                case Opcode.AIntBin:
                    int binOp = (ins.I0 >> 24) & 0xFF;
                    if (binOp is not (0 or 1 or 2 or 4 or 5))   // Add Sub Mul IntDiv Mod
                        throw new WasmCompileException($"a_int_bin op {binOp} at {ins.Pc}");
                    return;
                default:
                    throw new WasmCompileException($"{ins.Op} at {ins.Pc}");
            }
        }

        // ------------------------------------------------------------------
        // Leaders and cursors
        // ------------------------------------------------------------------

        public void AssignCursors()
        {
            // Every member's entry is a leader (its bias); operand addresses
            // are member-local, so they are biased here at collection.
            foreach (var m in _members) _leaders.Add(m.Bias);
            foreach (var ins in _instrs)
            {
                int b = Bias(ins);
                switch (ins.Op)
                {
                    case Opcode.SwitchOnTerm:
                        _leaders.Add(b + ins.I0); _leaders.Add(b + ins.I1);
                        _leaders.Add(b + ins.I2); _leaders.Add(b + ins.I3);
                        break;
                    case Opcode.SwitchOnArg:
                        _leaders.Add(b + ins.I1); _leaders.Add(b + ins.I2);
                        _leaders.Add(b + ins.I3); _leaders.Add(b + ins.I4);
                        break;
                    case Opcode.SwitchOnInteger:
                    case Opcode.SwitchOnAtom:
                    case Opcode.SwitchOnStructure:
                    case Opcode.SwitchOnIntegerArg:
                    case Opcode.SwitchOnAtomArg:
                    case Opcode.SwitchOnStructureArg:
                    case Opcode.SwitchOnAtomSub:
                    case Opcode.SwitchOnIntegerSub:
                    case Opcode.SwitchOnStructureSub:
                    {
                        int tableId = ins.Op switch
                        {
                            Opcode.SwitchOnInteger or Opcode.SwitchOnAtom
                                or Opcode.SwitchOnStructure => ins.I0,
                            // (argIdx, sub0, sub1, tableId)
                            Opcode.SwitchOnAtomSub or Opcode.SwitchOnIntegerSub
                                or Opcode.SwitchOnStructureSub => ins.I3,
                            _ => ins.I1,
                        };
                        var table = Sec(ins).Predicate.SwitchTables[tableId];
                        foreach (int v in table.Values) _leaders.Add(b + v);
                        _leaders.Add(b + table.DefaultAddress);
                        break;
                    }
                    case Opcode.Try:
                        _leaders.Add(b + ins.I0); _leaders.Add(ins.Pc + 9);
                        break;
                    case Opcode.Retry:
                        _leaders.Add(b + ins.I0); _leaders.Add(ins.Pc + 5);
                        break;
                    case Opcode.Trust:
                        _leaders.Add(b + ins.I0);
                        break;
                    case Opcode.Call:
                    case Opcode.CallBuiltin:
                        _leaders.Add(ins.Pc + 9);   // the return cursor
                        break;
                    case Opcode.TryMeElse:
                    case Opcode.RetryMeElse:
                        _leaders.Add(b + ins.I0);   // the else chain
                        break;
                    case Opcode.Jump:
                        _leaders.Add(b + ins.I0);
                        break;
                }
            }
            foreach (int addr in _leaders)
                if (!_byPc.ContainsKey(addr))
                    throw new WasmCompileException($"jump target {addr} is not an instruction boundary");

            // Cursor 0 is address 0 -- the fresh-entry convention resume
            // markers already use.
            int next = 0;
            foreach (int addr in _leaders)
                _cursorByAddr[addr] = next++;
            _failCase = next;
            _proceedCase = next + 1;

            // Cut partitions: accumulate whole members until the budget is
            // crossed. A member above the budget alone still gets exactly one
            // partition (it fit in a single function before grouping existed).
            var instrsPerSec = new int[_members.Count];
            foreach (var ins in _instrs) instrsPerSec[ins.Section]++;
            var addrs = new List<int>(_leaders);
            int start = 0; long cost = 0; int prevSec = -1;
            for (int i = 0; i < addrs.Count; i++)
            {
                int sec = _instrs[_byPc[addrs[i]]].Section;
                if (sec == prevSec) continue;
                if (cost >= PartitionBudgetWamInstrs && i > start)
                { _parts.Add((start, i)); start = i; cost = 0; }
                cost += instrsPerSec[sec];
                prevSec = sec;
            }
            _parts.Add((start, addrs.Count));

            // The PROCEED jump table: every in-group non-tail call bakes
            // Cp = marker(callerFid, resume cursor) as a constant; a proceed
            // whose Cp matches jumps straight to the caller's resume instead
            // of returning Success -- the interpreter's marker path, inside
            // the module. Foreign Cp values still return the verdict.
            foreach (var ins in _instrs)
            {
                if (ins.Op is not (Opcode.Call or Opcode.Execute)) continue;
                if (!_callee.TryGetValue(ins.Pc, out int callee)) continue;
                if (_env.TryGetBuiltin(callee, out _)) continue;
                // Who calls whom, counted at COMPILE time: the caller's and
                // callee's functors, one entry per call site. Free (this pass
                // already walks every instruction) and it is the evidence for
                // whether a group could be split along some boundary without
                // putting a host round trip on a hot path.
                var edge = (SelfFid(ins), callee);
                _callSites.TryGetValue(edge, out int seen);
                _callSites[edge] = seen + 1;
            }
        }

        private int CursorOf(int addr) => _cursorByAddr[addr];

        // ------------------------------------------------------------------
        // Emission
        // ------------------------------------------------------------------

        private List<Instruction> _code = new();
        private int _extraDepth;    // If/Block/Loop opened inside the current case
        private int _caseIndex;     // which case body is being emitted

        private void Op(Instruction i) => _code.Add(i);
        private void OpenIf() { Op(new If(BlockType.Empty)); _extraDepth++; }
        private void OpenIf(BlockType t) { Op(new If(t)); _extraDepth++; }
        private void OpenElse() { Op(new Else()); }
        private void CloseNested() { Op(new End()); _extraDepth--; }
        private void OpenBlock() { Op(new Block(BlockType.Empty)); _extraDepth++; }
        private void OpenLoop() { Op(new Loop(BlockType.Empty)); _extraDepth++; }

        /// <summary>Branch back to the dispatcher loop (LCur must be set).</summary>
        private void BrDispatch()
            => Op(new Branch((uint)(_extraDepth + (_caseCount - 1 - _caseIndex))));

        /// <summary>Resolves a resume marker through the resume table and, when
        /// it names THIS module, dispatches to its cursor. Leaves nothing on
        /// the stack and falls through when the marker resolves elsewhere or
        /// not at all — the caller then does whatever it did before there was
        /// a table.
        ///
        /// <para>A marker is already a dense id (EncodeResumeMarker interns the
        /// pair and returns Base + denseId), so this is a subscript rather than
        /// a search. What it replaces is a linear chain of baked comparisons,
        /// one per choice-point or return site in the module: on a big group
        /// that chain is long, and every failure walked it.</para>
        ///
        /// <para>Out-of-range is "not here", not a fault: a marker minted after
        /// this table was sized is newer than the module, and the host is the
        /// right place for it.</para></summary>
        private void EmitResumeProbe(uint markerLocal)
        {
            // i = marker - ResumeMarkerBase
            Op(new LocalGet(markerLocal));
            Op(new Int32Constant(Activation.ResumeMarkerBase));
            Op(new Int32Subtract());
            Op(new LocalSet(LT2));

            // if ((uint)i < length) { row = table[i]; ... }
            Op(new LocalGet(LT2));
            LoadSlot32(WasmAbi.ResumeTableLength);
            Op(new Int32LessThanUnsigned());
            OpenIf();
            {
                LoadSlot32(WasmAbi.ResumeTableBase);
                Op(new LocalGet(LT2));
                Op(new Int32Constant(3));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new Int64Load());
                Op(new LocalSet(LC2));                  // row

                // The row's high half is moduleId + 1; zero means no row.
                Op(new LocalGet(LC2));
                Op(new Int64Constant(32));
                Op(new Int64ShiftRightUnsigned());
                Op(new Int32WrapInt64());
                Op(new Int32Constant(1));
                Op(new Int32Subtract());
                Op(new LocalSet(LT0));                  // owner module id
                Op(new LocalGet(LT0));
                Op(new Int32Constant(_env.EncodeModuleId(_moduleId)));       // baked: see CompileGroup
                Op(new Int32Equal());
                OpenIf();
                {
                    Op(new LocalGet(LC2));
                    Op(new Int32WrapInt64());
                    Op(new LocalSet(LCur));
                    BrDispatch();
                }
                OpenElse();
                {
                    // Another module owns it. Look up where that module sits
                    // in this thread's function table and TAIL CALL it: the
                    // frame is replaced, not stacked, which is what lets a
                    // Prolog program cross modules millions of times. A plain
                    // call would grow the real stack per crossing.
                    //
                    // Measured: the hop is 6.1 ns and carrying the scalar set
                    // another 4.1, against the 4-15 us the same crossing costs
                    // going out to the host and back.
                    Op(new LocalGet(LT0));
                    Op(new Int32Constant(0));
                    Op(new Int32GreaterThanOrEqualSigned());
                    OpenIf();
                    {
                        LoadSlot32(WasmAbi.ModuleIndexBase);
                        Op(new LocalGet(LT0));
                        Op(new Int32Constant(2));
                        Op(new Int32ShiftLeft());
                        Op(new Int32Add());
                        Op(new Int32Load());
                        Op(new LocalSet(LT1));          // table index, -1 absent

                        // Slot 0 is a real slot: absence has to be -1, or the
                        // first module registered can never be reached.
                        Op(new LocalGet(LT1));
                        Op(new Int32Constant(0));
                        Op(new Int32GreaterThanOrEqualSigned());
                        OpenIf();
                        {
                            // The callee reloads the scalars in its prologue,
                            // so they have to be in the mailbox first.
                            StoreScalars();
                            StoreSlot64(WasmAbi.HopCount, () =>
                            {
                                LoadSlot64(WasmAbi.HopCount);
                                Op(new Int64Constant(1));
                                Op(new Int64Add());
                            });
                            Op(new LocalGet(0));                // mailbox
                            Op(new LocalGet(LC2));
                            Op(new Int32WrapInt64());           // its cursor
                            Op(new LocalGet(LT1));
                            Op(new ReturnCallIndirect(0));
                        }
                        CloseNested();
                    }
                    CloseNested();
                }
                CloseNested();
            }
            CloseNested();
        }

        private void GoTo(int addr)
        {
            Op(new Int32Constant(CursorOf(addr)));
            Op(new LocalSet(LCur));
            BrDispatch();
        }

        private void GoFail()
        {
            Op(new Int32Constant(_failCase));
            Op(new LocalSet(LCur));
            BrDispatch();
        }

        public byte[] Emit()
        {
            var module = new Module();
            module.Types.Add(new WebAssemblyType
            {
                Parameters = [WebAssemblyValueType.Int32, WebAssemblyValueType.Int32],
                Returns = [WebAssemblyValueType.Int32],
            });
            module.Imports.Add(new Import.Memory
            {
                Module = WasmAbi.MemoryModule,
                Field = WasmAbi.MemoryField,
                Type = new Memory(1, 65536),
            });
            // The thread's function table, where every module of this engine
            // is registered. A marker resolving to ANOTHER module is reached
            // through it, inside wasm, instead of by returning a verdict and
            // letting the host re-dispatch. The memory import stays FIRST:
            // WasmSharedMemory walks the import section for its limits byte.
            module.Imports.Add(new Import.Table(WasmAbi.TableModule,
                                               WasmAbi.TableField, 0, null));
            module.Types.Add(new WebAssemblyType
            {
                Parameters = [WebAssemblyValueType.Int64, WebAssemblyValueType.Int64,
                              WebAssemblyValueType.Int32],
                Returns = [WebAssemblyValueType.Int32],
            });
            // 0: run (the exported router); 1..K: partitions; K+1: the
            // fail/proceed resolver; K+2: the general unifier. All internal
            // but run.
            int k = _parts.Count;
            for (int f = 0; f <= k + 1; f++) module.Functions.Add(new Function { Type = 0 });
            module.Functions.Add(new Function { Type = 1 });
            module.Exports.Add(new Export
            {
                Kind = ExternalKind.Function, Index = 0, Name = WasmAbi.EntryExport,
            });

            module.Codes.Add(BuildDispatcherBody());
            var addrsInOrder = new List<int>(_leaders);
            foreach (var part in _parts)
                module.Codes.Add(BuildPartitionBody(part, addrsInOrder));
            module.Codes.Add(BuildResolverBody());
            module.Codes.Add(new FunctionBody
            {
                Locals =
                [
                    new Local { Count = 12, Type = WebAssemblyValueType.Int32 },
                    new Local { Count = 4, Type = WebAssemblyValueType.Int64 },
                ],
                Code = BuildUnifierBody(),
            });

            using var ms = new MemoryStream();
            module.WriteToBinary(ms);
            return ms.ToArray();
        }

        /// <summary>run(mailbox, cursor): route the cursor to the partition
        /// owning it (ranges are ascending, so a chain of upper-bound tests;
        /// a pseudo-cursor goes to the resolver) with a TAIL call. Nothing
        /// of this function survives the transfer: a partition leaving for
        /// another partition tail-calls run again, and a hop to another
        /// module is a return_call_indirect from a partition, so the module
        /// holds ONE frame at any depth of backtracking. A plain call here
        /// would keep a run frame per hop and grow the stack with the
        /// choice-point chain.</summary>
        private FunctionBody BuildDispatcherBody()
        {
            _code = new List<Instruction>();
            Op(new LocalGet(1));
            Op(new Int32Constant(_failCase));
            Op(new Int32GreaterThanOrEqualSigned());
            OpenIf();
            {
                Op(new LocalGet(0)); Op(new LocalGet(1));
                Op(new ReturnCall((uint)(_parts.Count + 1)));
            }
            CloseNested();
            for (int p = 0; p < _parts.Count - 1; p++)
            {
                Op(new LocalGet(1));
                Op(new Int32Constant(_parts[p].Hi));
                Op(new Int32LessThanSigned());
                OpenIf();
                {
                    Op(new LocalGet(0)); Op(new LocalGet(1));
                    Op(new ReturnCall((uint)(1 + p)));
                }
                CloseNested();
            }
            Op(new LocalGet(0)); Op(new LocalGet(1));
            Op(new ReturnCall((uint)_parts.Count));
            Op(new End());                                  // the function
            var body = new FunctionBody { Locals = [], Code = _code };
            _extraDepth = 0;
            return body;
        }

        private static Local[] EngineLocals() =>
        [
            new Local { Count = 16, Type = WebAssemblyValueType.Int32 },
            new Local { Count = 3, Type = WebAssemblyValueType.Int64 },
            new Local { Count = 2, Type = WebAssemblyValueType.Int32 },
            new Local { Count = AEvalMaxDepth, Type = WebAssemblyValueType.Int64 },
            new Local { Count = 2, Type = WebAssemblyValueType.Int64 },
            // One KIND per a_eval slot (0 = the 60-bit int lane, 1 = float
            // bits). Appended last: every index above is baked into emitted
            // code, so the bank can only grow at the end.
            new Local { Count = AEvalMaxDepth, Type = WebAssemblyValueType.Int32 },
            // The attribute probe's slot index and the value it found. Same
            // rule: appended after the kind bank, never before it.
            new Local { Count = 2, Type = WebAssemblyValueType.Int32 },
            new Local { Count = 1, Type = WebAssemblyValueType.Int64 },
            // The meta-call cache probe's base, its bound, and the goal's
            // own arity. Appended after the attribute probe's, same rule:
            // only at the end.
            new Local { Count = 3, Type = WebAssemblyValueType.Int32 },
            // The cut barrier '$call'/2 carries, read out of X1 BEFORE the
            // goal's arguments overwrite it. Appended last, same rule.
            new Local { Count = 1, Type = WebAssemblyValueType.Int64 },
            // The meta-called goal's heap base and its functor cell, in that
            // order. Appended last, same rule.
            new Local { Count = 1, Type = WebAssemblyValueType.Int32 },
            new Local { Count = 1, Type = WebAssemblyValueType.Int64 },
            // $dom_same's first cell, held across the second argument's
            // deref. Appended last, same rule.
            new Local { Count = 1, Type = WebAssemblyValueType.Int64 },
            // And its second. Appended last, same rule.
            new Local { Count = 1, Type = WebAssemblyValueType.Int64 },
            // The second domain's heap index, for the contents comparison.
            // Appended last, same rule.
            new Local { Count = 1, Type = WebAssemblyValueType.Int32 },
            // The copy cursor when a domain is rebuilt, and the two the
            // count-changing rebuild needs beside it. Appended last, same
            // rule.
            new Local { Count = 3, Type = WebAssemblyValueType.Int32 },
            // Whether the meta-called goal is an ATOM, which keys the
            // cache differently. Appended last, same rule.
            new Local { Count = 1, Type = WebAssemblyValueType.Int32 },
        ];

        /// <summary>One partition: prologue, dispatch loop, br_table over the
        /// partition's own cursors, a LOCAL fail case (this partition's CP
        /// sites only), and an $out case that spills the scalars and
        /// tail-calls run with the cursor to route — how a jump reaches
        /// another partition. In-partition jumps stay internal branches.</summary>
        private FunctionBody BuildPartitionBody((int Lo, int Hi) part,
                                                List<int> addrsInOrder)
        {
            _code = new List<Instruction>();
            _curPart = part;
            int n = part.Hi - part.Lo;
            _caseCount = n + 2;                             // + $fail + $out

            EmitPrologue();
            OpenLoop();                                     // never popped via CloseNested
            _extraDepth--;                                  // accounted in BrDispatch instead
            if (DebugLoopGuard)
            {
                // DIAGNOSTIC (off by default): every dispatch records the
                // cursor in DebugGuardCursor and bumps DebugGuardCount; when
                // it passes DebugGuardLimit (10M when the host leaves it 0)
                // the run returns the impossible verdict 99. These had been
                // bare slot numbers, and the numbers had since been given to
                // the diagnostic tallies -- turning the guard on would have
                // corrupted them.
                // Turns an in-module infinite loop into a readable report,
                // and with a host-set limit it single-steps a run by
                // dispatch count.
                StoreSlot64(WasmAbi.DebugGuardCursor, () =>
                {
                    Op(new LocalGet(LCur));
                    Op(new Int64ExtendInt32Signed());
                });
                StoreSlot64(WasmAbi.DebugGuardCount, () =>
                {
                    LoadSlot64(WasmAbi.DebugGuardCount);
                    Op(new Int64Constant(1));
                    Op(new Int64Add());
                });
                LoadSlot64(WasmAbi.DebugGuardCount);
                LoadSlot64(WasmAbi.DebugGuardLimit);
                Op(new Int64Constant(0));
                Op(new Int64GreaterThanSigned());
                OpenIf(BlockType.Int64);
                LoadSlot64(WasmAbi.DebugGuardLimit);
                OpenElse();
                Op(new Int64Constant(10_000_000));
                CloseNested();
                Op(new Int64GreaterThanSigned());
                OpenIf();
                Op(new Int32Constant(99));
                Op(new Return());
                CloseNested();
            }
            for (int j = _caseCount - 1; j >= 0; j--) Op(new Block(BlockType.Empty));
            // Route: FAIL pseudo-cursor -> local $fail; anything outside
            // [Lo, Hi) (the PROCEED pseudo-cursor included) -> $out; a local
            // cursor -> its case via br_table. Depths at this point,
            // innermost first: cases 0..n-1, $fail = n, $out = n+1 (+1
            // inside an If).
            Op(new LocalGet(LCur));
            Op(new Int32Constant(_failCase));
            Op(new Int32Equal());
            OpenIf();
            Op(new Branch((uint)(n + 1)));                  // $fail
            CloseNested();
            Op(new LocalGet(LCur));
            Op(new Int32Constant(part.Lo));
            Op(new Int32Subtract());
            Op(new LocalTee(LT0));
            Op(new Int32Constant(n));
            Op(new Int32GreaterThanOrEqualUnsigned());      // negative wraps huge
            OpenIf();
            Op(new Branch((uint)(n + 2)));                  // $out
            CloseNested();
            Op(new LocalGet(LT0));
            var labels = new uint[n];
            for (uint j = 0; j < n; j++) labels[j] = j;
            Op(new BranchTable((uint)(n + 1), labels));     // default unreachable

            for (int j = 0; j < n; j++)
            {
                Op(new End());
                _caseIndex = j;
                EmitRun(addrsInOrder[part.Lo + j]);
            }
            Op(new End());
            _caseIndex = n;
            EmitFailCase(missReturnsToHost: false);
            Op(new End());                                  // $out
            _caseIndex = n + 1;
            EmitContinueReturn();
            Op(new End());                                  // the loop
            EmitReturn(WasmVerdict.Fail);                   // unreachable fallback
            Op(new End());                                  // the function
            var body = new FunctionBody { Locals = EngineLocals(), Code = _code };
            _extraDepth = 0;
            return body;
        }

        /// <summary>The shared resolver: where a fail or a proceed that no
        /// partition resolved locally ends up, and the only place that can
        /// answer the host. It was once the ONLY full copy of two group-wide
        /// chains, which is why it is a function of its own; the chains are
        /// now indexed reads into the resume table, so what it saves is no
        /// longer size but the routing -- a partition resolves its own
        /// backtracking without leaving the function, and anything else
        /// arrives here.</summary>
        private FunctionBody BuildResolverBody()
        {
            _code = new List<Instruction>();
            _curPart = (0, 0);
            _caseCount = 3;                                 // $proceed, $fail, $out

            EmitPrologue();
            OpenLoop();
            _extraDepth--;
            Op(new Block(BlockType.Empty));                 // $out
            Op(new Block(BlockType.Empty));                 // $fail
            Op(new Block(BlockType.Empty));                 // $proceed
            Op(new LocalGet(LCur));
            Op(new Int32Constant(_failCase));
            Op(new Int32Equal());
            OpenIf();
            Op(new Branch(2));                              // $fail
            CloseNested();
            Op(new LocalGet(LCur));
            Op(new Int32Constant(_proceedCase));
            Op(new Int32Equal());
            OpenIf();
            Op(new Branch(1));                              // $proceed
            CloseNested();
            Op(new Branch(2));                              // $out (a resolved cursor)
            Op(new End());                                  // $proceed
            _caseIndex = 0;
            EmitProceedResolve();
            Op(new End());                                  // $fail
            _caseIndex = 1;
            EmitFailCase(missReturnsToHost: true);
            Op(new End());                                  // $out
            _caseIndex = 2;
            EmitContinueReturn();
            Op(new End());                                  // the loop
            EmitReturn(WasmVerdict.Fail);                   // unreachable fallback
            Op(new End());                                  // the function
            var body = new FunctionBody { Locals = EngineLocals(), Code = _code };
            _extraDepth = 0;
            return body;
        }

        /// <summary>The resolver's $proceed: the full marker chain. A hit
        /// jumps (via $out) to the resume cursor's partition; a miss is a
        /// genuinely foreign Cp — Success to the host, exactly the
        /// single-function module's answer.</summary>
        private void EmitProceedResolve()
        {
            // Identical to what a partition emits now. The resolver existed to
            // hold the ONE full copy of a chain no partition could afford to
            // carry; a table read is the same handful of instructions
            // everywhere, so there is no longer a full copy to hold. The
            // function stays because the dispatcher routes the pseudo-cursors
            // here, and removing it is a separate cleanup.
            Op(new LocalGet(LH));
            LoadSlot32(WasmAbi.HeapWatermark);
            Op(new Int32LessThanSigned());
            OpenIf();
            EmitResumeProbe(LCP);
            CloseNested();
            EmitReturn(WasmVerdict.Success);
        }

        // ---- mailbox access ----

        private void LoadSlot64(int slot)
        { Op(new LocalGet(0)); Op(new Int64Load { Offset = WasmAbi.ByteOffset(slot) }); }

        private void LoadSlot32(int slot)
        { LoadSlot64(slot); Op(new Int32WrapInt64()); }

        /// <summary>mailbox[slot] = (i64) value pushed by <paramref name="value"/>.</summary>
        private void StoreSlot64(int slot, Action value)
        {
            Op(new LocalGet(0));
            value();
            Op(new Int64Store { Offset = WasmAbi.ByteOffset(slot) });
        }

        private void StoreSlotFromI32Local(int slot, uint local)
            => StoreSlot64(slot, () =>
            {
                Op(new LocalGet(local));
                Op(new Int64ExtendInt32Signed());
            });

        private void EmitPrologue()
        {
            Op(new LocalGet(1)); Op(new LocalSet(LCur));
            LoadSlot32(WasmAbi.HeapBase); Op(new LocalSet(LHeapB));
            LoadSlot32(WasmAbi.StackBase); Op(new LocalSet(LStackB));
            LoadSlot32(WasmAbi.RegistersBase); Op(new LocalSet(LRegsB));
            LoadSlot32(WasmAbi.BindingTrailBase); Op(new LocalSet(LTrailB));
            LoadSlot32(WasmAbi.HeapTop); Op(new LocalSet(LH));
            LoadSlot32(WasmAbi.TrailTop); Op(new LocalSet(LTR));
            LoadSlot32(WasmAbi.EnvTop); Op(new LocalSet(LE));
            LoadSlot32(WasmAbi.ChoiceTop); Op(new LocalSet(LB));
            LoadSlot32(WasmAbi.HeapBacktrack); Op(new LocalSet(LHB));
            LoadSlot32(WasmAbi.StackTop); Op(new LocalSet(LST));
            LoadSlot32(WasmAbi.ContinuationPc); Op(new LocalSet(LCP));
            LoadSlot32(WasmAbi.WriteMode); Op(new LocalSet(LMode));
            LoadSlot32(WasmAbi.UnifyPointer); Op(new LocalSet(LS));
            LoadSlot64(WasmAbi.GoalsRun); Op(new LocalSet(LGoals));
            // Cells claimed = (H at exit - H at entry) + every span a
            // backtrack discarded. Seeding with -H here and adding H back in
            // the epilogue gives the first term with no extra local and no
            // per-allocation work: claiming a cell is still just H++.
            LoadSlot64(WasmAbi.CellsClaimed);
            LoadSlot32(WasmAbi.HeapTop); Op(new Int64ExtendInt32Signed());
            Op(new Int64Subtract());
            Op(new LocalSet(LCells));
        }

        private void StoreScalars()
        {
            StoreSlot64(WasmAbi.GoalsRun, () => Op(new LocalGet(LGoals)));
            StoreSlot64(WasmAbi.CellsClaimed, () =>
            {
                Op(new LocalGet(LCells));
                Op(new LocalGet(LH)); Op(new Int64ExtendInt32Signed());
                Op(new Int64Add());
            });
            StoreSlotFromI32Local(WasmAbi.HeapTop, LH);
            StoreSlotFromI32Local(WasmAbi.TrailTop, LTR);
            StoreSlotFromI32Local(WasmAbi.EnvTop, LE);
            StoreSlotFromI32Local(WasmAbi.ChoiceTop, LB);
            StoreSlotFromI32Local(WasmAbi.HeapBacktrack, LHB);
            StoreSlotFromI32Local(WasmAbi.StackTop, LST);
            StoreSlotFromI32Local(WasmAbi.ContinuationPc, LCP);
            StoreSlotFromI32Local(WasmAbi.WriteMode, LMode);
            StoreSlotFromI32Local(WasmAbi.UnifyPointer, LS);
        }

        private void EmitReturn(WasmVerdict v)
        {
            StoreScalars();
            // After in-wasm hops the host cannot know which module it is
            // hearing from, and a deopt pc or a builtin's return address is
            // in THAT module's build space. Every verdict says. Not on the
            // hop path: a hop reloads nothing from this slot.
            StoreSlot64(WasmAbi.CurrentModuleId, () => Op(new Int64Constant(_env.EncodeModuleId(_moduleId))));
            Op(new Int32Constant((int)v));
            Op(new Return());
        }

        /// <summary>Spill the scalars and tail-call run with LCur: "not
        /// mine, continue at this cursor" — the cross-partition transfer.
        /// The next partition's prologue reloads what this stored. A tail
        /// call, so the transfer replaces this frame instead of stacking
        /// under a dispatcher loop.</summary>
        private void EmitContinueReturn()
        {
            StoreScalars();
            Op(new LocalGet(0));
            Op(new LocalGet(LCur));
            Op(new ReturnCall(0));
        }

        /// <summary>A deopt site that follows a MetaGuard: DiagA already
        /// carries the guard's code, and stamping here would overwrite the
        /// specific cause with a general one.</summary>
        private const int DeoptStamped = -1;

        /// <summary>The reason is REQUIRED, and that is the point: every
        /// step-aside says why, in the same histogram the meta-call guards
        /// feed, or names <see cref="DeoptStamped"/> to say a guard already
        /// did. Half of clpr's deopts read as anonymous because sites were
        /// stamped one by one as each was suspected -- a parameter the
        /// compiler enforces cannot leave one out.</summary>
        private void EmitDeopt(int bytecodePc, int reason)
        {
            if (reason != DeoptStamped) MetaGuard(reason);
            StoreSlot64(WasmAbi.Pc, () => Op(new Int64Constant(_env.EncodeAddress(bytecodePc))));
            EmitReturn(WasmVerdict.Deopt);
        }

        // ---- cells ----

        /// <summary>Pushes the address of cell <c>area[indexLocal]</c>.</summary>
        private void CellAddr(uint baseLocal, uint indexLocal)
        {
            Op(new LocalGet(baseLocal));
            Op(new LocalGet(indexLocal));
            Op(new Int32Constant(3));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
        }

        /// <summary>Pushes cell <c>area[indexLocal + k]</c> (k a small constant).</summary>
        private void CellLoadDyn(uint baseLocal, uint indexLocal, int k = 0)
        {
            CellAddr(baseLocal, indexLocal);
            Op(new Int64Load { Offset = (uint)(k * 8) });
        }

        /// <summary><c>area[indexLocal + k] = value()</c>.</summary>
        private void CellStoreDyn(uint baseLocal, uint indexLocal, int k, Action value)
        {
            CellAddr(baseLocal, indexLocal);
            value();
            Op(new Int64Store { Offset = (uint)(k * 8) });
        }

        // Highest X register the module touches: the host must guarantee the
        // register area covers it BEFORE entering (the bank starts small and
        // an out-of-range wasm store corrupts whatever lies next).
        private int _maxRegister = -1;
        public int RegisterDemand
        {
            get
            {
                int d = _maxRegister + 1;
                foreach (var m in _members)
                    if (m.Predicate.Arity > d) d = m.Predicate.Arity;
                return d;
            }
        }

        private void RegLoad(int reg)
        {
            if (reg > _maxRegister) _maxRegister = reg;
            Op(new LocalGet(LRegsB)); Op(new Int64Load { Offset = (uint)(reg * 8) });
        }

        private void RegStore(int reg, Action value)
        {
            if (reg > _maxRegister) _maxRegister = reg;
            Op(new LocalGet(LRegsB));
            value();
            Op(new Int64Store { Offset = (uint)(reg * 8) });
        }

        /// <summary>Pushes the cell in Y[slot] of the current frame.</summary>
        private void YLoad(int slot)
        { CellAddr(LStackB, LE); Op(new Int64Load { Offset = (uint)((3 + slot) * 8) }); }

        private void YStore(int slot, Action value)
        {
            CellAddr(LStackB, LE);
            value();
            Op(new Int64Store { Offset = (uint)((3 + slot) * 8) });
        }

        /// <summary>RawInt(v) for an i32 pushed by <paramref name="value"/> --
        /// the control-word encoding frames and choice points use.</summary>
        private void RawInt(Action value)
        {
            value();
            Op(new Int64ExtendInt32Signed());
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int64Constant(RawIntTag));
            Op(new Int64Or());
        }

        // ---- deref ----

        /// <summary>Derefs LC0 in place; LDa ends at the last REF's home (the
        /// unbound address when LC0 comes out still a REF). A REF cell IS its
        /// heap index (tag 0), which is what makes the self-reference test a
        /// plain i64 compare.</summary>
        private void Deref()
        {
            OpenBlock();                                    // $done
            OpenLoop();                                     // $follow
            Op(new LocalGet(LC0));
            Op(new Int64Constant(60));
            Op(new Int64ShiftRightUnsigned());
            Op(new Int64Constant(0));
            Op(new Int64NotEqual());
            Op(new BranchIf(1));                            // not a REF -> done
            Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LDa));
            CellLoadDyn(LHeapB, LDa);
            Op(new LocalSet(LC1));
            Op(new LocalGet(LC1)); Op(new LocalGet(LC0)); Op(new Int64Equal());
            Op(new BranchIf(1));                            // self-reference -> done
            Op(new LocalGet(LC1)); Op(new LocalSet(LC0));
            Op(new Branch(0));
            CloseNested();                                  // loop
            CloseNested();                                  // block
        }

        /// <summary>Pushes LC0's tag as i32.</summary>
        private void TagOfC0()
        {
            Op(new LocalGet(LC0));
            Op(new Int64Constant(60));
            Op(new Int64ShiftRightUnsigned());
            Op(new Int32WrapInt64());
        }

        // ---- unify a derefed LC0 against a constant cell ----

        /// <summary>LC0 has been derefed. Unifies it with the constant cell:
        /// falls through on success, branches to FAIL on mismatch, deopts on
        /// an attributed variable, binds (with the young-to-old trail rule)
        /// when unbound.</summary>
        private void UnifyC0WithConst(long constCell, int pcForDeopt)
        {
            Op(new LocalGet(LC0));
            Op(new Int64Constant(constCell));
            Op(new Int64NotEqual());
            OpenIf();
            {
                TagOfC0();
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                OpenIf();                                   // unbound: bind it
                EmitBindDa(pcForDeopt, () => Op(new Int64Constant(constCell)));
                OpenElse();
                {
                    TagOfC0();
                    Op(new Int32Constant((int)Tag.AttVar));
                    Op(new Int32Equal());
                    OpenIf();
                    EmitDeopt(pcForDeopt, 24);
                    CloseNested();
                    GoFail();
                }
                CloseNested();
            }
            CloseNested();
        }

        /// <summary>Pushes LDa onto the binding trail (capacity checked).</summary>
        private void EmitTrailDa(int pcForDeopt)
        {
            Op(new LocalGet(LTR));
            LoadSlot32(WasmAbi.TrailLimit);
            Op(new Int32GreaterThanOrEqualSigned());
            OpenIf();
            EmitDeopt(pcForDeopt, 22);
            CloseNested();
            // trail[TR] = da (a 4-byte entry); TR++
            Op(new LocalGet(LTrailB));
            Op(new LocalGet(LTR));
            Op(new Int32Constant(2));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new LocalGet(LDa));
            Op(new Int32Store());
            Op(new LocalGet(LTR)); Op(new Int32Constant(1)); Op(new Int32Add());
            Op(new LocalSet(LTR));
        }

        // ------------------------------------------------------------------
        // The FAIL case: local backtracking
        // ------------------------------------------------------------------

        /// <summary>No choice point of OURS on top means the host backtracks;
        /// one of ours means its BP names a retry/trust cursor and the
        /// restore there does the rest. BP values are compared against this
        /// module's own encodings -- anything else is foreign.
        ///
        /// <para>The body is the same wherever it is emitted; only a MISS
        /// differs. In the resolver a miss is a CP no member pushed, so the
        /// verdict goes to the host; in a partition it continues, and the
        /// dispatcher routes it. Measured at ~237 wasm instructions a copy:
        /// noise in a group module (8 copies, 1898 instructions of 4.4 MB),
        /// part of the fixed furniture in a one-predicate module. Routing a
        /// partition's miss to the resolver instead would save that and put
        /// a call on every failure, which is the hot path.</para></summary>
        private void EmitFailCase(bool missReturnsToHost)
        {
            Op(new LocalGet(LB));
            Op(new Int32Constant(0));
            Op(new Int32LessThanSigned());
            OpenIf();
            EmitReturn(WasmVerdict.Fail);
            CloseNested();

            // bp = (i32)stack[B + 1 + arity + 3]
            CellLoadDyn(LStackB, LB);
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT0));                          // arity
            CellAddr(LStackB, LB);
            Op(new LocalGet(LT0));
            Op(new Int32Constant(3));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new LocalSet(LT2));                          // &stack[B + arity]
            // No extra-trail guard here: the retry/trust case this jumps to
            // opens with EmitRestoreCommon, which already steps aside when
            // the CP's extra-trail top differs from the live one (the module
            // can only unwind the binding trail). A guard here as well cost
            // 100x on the phase-B bench when it compared the RAW ctl cell
            // (RawInt, tag bits included) against the plain mailbox top and
            // so fired on every failure.
            Op(new LocalGet(LT2));
            Op(new Int64Load { Offset = 4 * 8 });           // ctl[3] = BP (1+arity handled: base+arity*8, +1 cell +3 cells)
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));                          // bp

            // BP values bake the site's OWN fid, so the pairs map value ->
            // cursor across every member. A partition chains only the pairs
            // whose retry cursor is local (self-backtracking, the hot case)
            // and hands a miss to the resolver; the resolver chains them ALL
            // and only a CP no member pushed returns Fail to the host.
            // One indexed read, where a chain of baked comparisons used to be:
            // one `if (bp == const)` per choice-point site in the module,
            // walked linearly on EVERY failure. A BP is a resume marker and a
            // marker is a dense id, so the answer is a subscript.
            //
            // The probe dispatches to any cursor of this module, including one
            // in another partition -- the br_table's default hands those to the
            // group dispatcher, which is the same route a jump across
            // partitions already takes. So the partition/resolver split that
            // the chain needed does not apply to what is EMITTED any more:
            // the copies are identical but for the miss.
            EmitResumeProbe(LT1);
            if (missReturnsToHost) EmitReturn(WasmVerdict.Fail);   // a foreign CP
            else EmitContinueReturn();                             // LCur is still FAIL
        }

        // ------------------------------------------------------------------
        // A straight-line run from one leader to the next
        // ------------------------------------------------------------------

        private void EmitRun(int startAddr)
        {
            _aevalDepth = 0;
            int i = _byPc[startAddr];
            while (true)
            {
                var ins = _instrs[i];
                if (ins.Op is Opcode.PutStructureR or Opcode.PutListR)
                {
                    i = EmitReservedRegion(i);
                    if (i >= _instrs.Count)
                        throw new WasmCompileException($"fell off the end after a reserved build at {ins.Pc}");
                    int afterPc = _instrs[i].Pc;
                    if (_cursorByAddr.ContainsKey(afterPc)) { GoTo(afterPc); return; }
                    continue;
                }
                bool transferred = EmitInstr(ins);
                if (transferred)
                {
                    if (_aevalDepth != 0)
                        throw new WasmCompileException($"a_eval sequence cut by {ins.Op} at {ins.Pc}");
                    return;
                }
                i++;
                if (i >= _instrs.Count)
                    throw new WasmCompileException($"fell off the end after {ins.Op} at {ins.Pc}");
                int nextPc = _instrs[i].Pc;
                if (_cursorByAddr.ContainsKey(nextPc))
                {
                    // A leader is an external re-entry point: locals do not
                    // survive it, so an open RPN sequence cannot cross one.
                    if (_aevalDepth != 0)
                        throw new WasmCompileException($"a_eval sequence crosses leader {nextPc}");
                    GoTo(nextPc); return;
                }
            }
        }

        /// <summary>Emits one instruction; true when it transferred control.</summary>
        /// <summary>Resume addresses at which an inline meta-call left a
        /// frame of its OWN to pop. Reached only by that meta-call's return:
        /// the site never exits to the host (its fallback is a step-aside,
        /// which continues in the interpreter and does not re-enter here), so
        /// there is no second path arriving without a frame.</summary>
        private readonly HashSet<int> _metaFrameResume = new();

        private bool EmitInstr(Instr ins)
        {
            // The meta-call's frame is popped where control comes BACK, which
            // is a dispatch case of its own -- CP and E are restored from it
            // before the clause continues.
            if (_metaFrameResume.Contains(ins.Pc)) EmitDeallocate();
            switch (ins.Op)
            {
                case Opcode.SwitchOnTerm:
                {
                    int b = Bias(ins);
                    EmitSwitchOnTerm(ins.Pc, 0,
                        b + ins.I0, b + ins.I1, b + ins.I2, b + ins.I3);
                    return true;
                }
                case Opcode.SwitchOnArg:
                {
                    int b = Bias(ins);
                    EmitSwitchOnTerm(ins.Pc, ins.I0,
                        b + ins.I1, b + ins.I2, b + ins.I3, b + ins.I4);
                    return true;
                }
                case Opcode.SwitchOnInteger: EmitSwitchOnInteger(ins, 0, ins.I0); return true;
                case Opcode.SwitchOnIntegerArg: EmitSwitchOnInteger(ins, ins.I0, ins.I1); return true;
                case Opcode.SwitchOnAtom: EmitSwitchOnAtom(ins, 0, ins.I0); return true;
                case Opcode.SwitchOnAtomArg: EmitSwitchOnAtom(ins, ins.I0, ins.I1); return true;
                case Opcode.SwitchOnStructure: EmitSwitchOnStructure(ins, 0, ins.I0); return true;
                case Opcode.SwitchOnStructureArg: EmitSwitchOnStructure(ins, ins.I0, ins.I1); return true;
                case Opcode.SwitchOnAtomSub: EmitSwitchOnConstSub(ins, atoms: true); return true;
                case Opcode.SwitchOnIntegerSub: EmitSwitchOnConstSub(ins, atoms: false); return true;
                case Opcode.SwitchOnStructureSub: EmitSwitchOnStructureSub(ins); return true;
                case Opcode.Try: EmitTry(ins); return true;
                case Opcode.Retry: EmitRetry(ins); return true;
                case Opcode.Trust: EmitTrust(ins); return true;
                case Opcode.Allocate: EmitAllocate(ins); return false;
                case Opcode.Deallocate: EmitDeallocate(); return false;
                case Opcode.DeallocateProceed:
                    EmitFlagsCheck(ins.Pc);
                    EmitDeallocate();
                    EmitProceedReturn();
                    return true;
                case Opcode.Proceed: EmitProceed(ins); return true;
                case Opcode.GetVariableY:
                    YStore(ins.I0, () => RegLoad(ins.I1)); return false;
                case Opcode.GetValueY:
                    EmitUnifyTwo(() => YLoad(ins.I0), () => RegLoad(ins.I1), ins.Pc);
                    return false;
                case Opcode.GetValueX:
                    EmitUnifyTwo(() => RegLoad(ins.I0), () => RegLoad(ins.I1), ins.Pc);
                    return false;
                case Opcode.GetVariableX:
                    RegStore(ins.I0, () => RegLoad(ins.I1)); return false;
                case Opcode.PutValueY:
                    RegStore(ins.I1, () => YLoad(ins.I0)); return false;
                case Opcode.PutValueX:
                    RegStore(ins.I1, () => RegLoad(ins.I0)); return false;
                case Opcode.PutVariableY: EmitPutVariableY(ins); return false;
                case Opcode.PutVariableX: EmitPutVariableX(ins); return false;
                case Opcode.PutInteger:
                    RegStore(ins.I1, () => Op(new Int64Constant(Cell.Int(ins.I0).Data)));
                    return false;
                case Opcode.PutAtom:
                    RegStore(ins.I1, () => Op(new Int64Constant(_env.AtomCell(ins.I0))));
                    return false;
                case Opcode.PutNil:
                    RegStore(ins.I0, () => Op(new Int64Constant(_env.AtomCell(AtomTable.EmptyListId))));
                    return false;
                case Opcode.GetInteger:
                    RegLoad(ins.I1); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(Cell.Int(ins.I0).Data, ins.Pc);
                    return false;
                case Opcode.GetAtom:
                    RegLoad(ins.I1); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(_env.AtomCell(ins.I0), ins.Pc);
                    return false;
                case Opcode.GetNil:
                    RegLoad(ins.I0); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(_env.AtomCell(AtomTable.EmptyListId), ins.Pc);
                    return false;
                case Opcode.AIntCmp: EmitAIntCmp(ins); return false;
                case Opcode.Meta: return false;   // metadata; nothing runs
                case Opcode.GetStructure: EmitGetStructure(ins.I0, ins.I1, ins.Pc); return false;
                case Opcode.GetList: EmitGetList(ins.I0, ins.Pc); return false;
                case Opcode.GetListA1: EmitGetList(0, ins.Pc); return false;
                case Opcode.GetListA2: EmitGetList(1, ins.Pc); return false;
                case Opcode.PutStructure: EmitPutStructure(ins.I0, ins.I1, ins.Pc); return false;
                case Opcode.PutList:
                    // The register takes a LIS pointing at the NEXT two heap
                    // cells; the two unify_* that follow write them.
                    RegStore(ins.I0, () =>
                    {
                        Op(new LocalGet(LH)); Op(new Int64ExtendInt32Unsigned());
                        Op(new Int64Constant((long)Tag.Lis << Cell.TagShift));
                        Op(new Int64Or());
                    });
                    Op(new Int32Constant(1)); Op(new LocalSet(LMode));
                    Op(new LocalGet(LH)); Op(new LocalSet(LS));
                    return false;
                case Opcode.UnifyVariableX:
                    EmitUnifyVariable(ins.Pc, write: () => RegStore(ins.I0, () => Op(new LocalGet(LC0))),
                                      read: () => RegStore(ins.I0, () => Op(new LocalGet(LC0))));
                    return false;
                case Opcode.UnifyVariableY:
                    EmitUnifyVariable(ins.Pc, write: () => YStore(ins.I0, () => Op(new LocalGet(LC0))),
                                      read: () => YStore(ins.I0, () => Op(new LocalGet(LC0))));
                    return false;
                case Opcode.UnifyValueX:
                    EmitUnifyValue(ins.Pc, () => RegLoad(ins.I0));
                    return false;
                case Opcode.UnifyValueY:
                    EmitUnifyValue(ins.Pc, () => YLoad(ins.I0));
                    return false;
                case Opcode.UnifyAtom:
                case Opcode.UnifyConstant:
                    EmitUnifyConst(_env.AtomCell(ins.I0), ins.Pc); return false;
                case Opcode.UnifyInteger:
                    EmitUnifyConst(Cell.Int(ins.I0).Data, ins.Pc); return false;
                case Opcode.UnifyNil:
                    EmitUnifyConst(_env.AtomCell(AtomTable.EmptyListId), ins.Pc); return false;
                case Opcode.UnifyVoid: EmitUnifyVoid(ins.I0, ins.Pc); return false;
                case Opcode.UnifyStructure: EmitUnifyStructure(ins.I0, ins.Pc); return false;
                case Opcode.UnifyList: EmitUnifyList(ins.Pc); return false;
                case Opcode.GetConstantA1:
                    RegLoad(0); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(_env.AtomCell(ins.I0), ins.Pc); return false;
                case Opcode.GetConstantA2:
                    RegLoad(1); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(_env.AtomCell(ins.I0), ins.Pc); return false;
                case Opcode.PutConstantA1:
                    RegStore(0, () => Op(new Int64Constant(_env.AtomCell(ins.I0))));
                    return false;
                case Opcode.PutConstantA2:
                    RegStore(1, () => Op(new Int64Constant(_env.AtomCell(ins.I0))));
                    return false;
                case Opcode.DeallocateExecute:
                    EmitFlagsCheck(ins.Pc);
                    EmitDeallocate();
                    EmitExecuteTail(ins.Pc);
                    return true;
                case Opcode.NeckCut:
                    EmitFlagsCheck(ins.Pc);
                    EmitCut(() => LoadSlot32(WasmAbi.CutBarrier));
                    return false;
                case Opcode.Cut:
                    EmitFlagsCheck(ins.Pc);
                    EmitCut(() => { YLoad(ins.I0); Op(new Int32WrapInt64()); });
                    return false;
                case Opcode.GetLevel:
                    YStore(ins.I0, () => RawInt(() => LoadSlot32(WasmAbi.CutBarrier)));
                    return false;
                case Opcode.GetLevelB:
                    YStore(ins.I0, () => RawInt(() => Op(new LocalGet(LB))));
                    return false;
                case Opcode.AllocateGetLevel:
                    EmitAllocate(ins);   // I0 = count, same operand slot
                    YStore(ins.I1, () => RawInt(() => LoadSlot32(WasmAbi.CutBarrier)));
                    return false;
                case Opcode.CutProceed:
                    EmitFlagsCheck(ins.Pc);
                    EmitCut(() => { YLoad(ins.I0); Op(new Int32WrapInt64()); });
                    EmitProceedReturn();
                    return true;
                case Opcode.CutDeallocateProceed:
                    EmitFlagsCheck(ins.Pc);
                    EmitCut(() => { YLoad(ins.I0); Op(new Int32WrapInt64()); });
                    EmitDeallocate();
                    EmitProceedReturn();
                    return true;
                case Opcode.CallBuiltin:
                    // The id is the operand itself: the module compiler
                    // resolves builtins when the registry is loaded, exactly
                    // as the linker would. The env-trim count (I1) rides the
                    // high half of the id slot; -1 is the no-trim sentinel.
                    if (_env.IsInlineUnify(ins.I0))
                    {
                        // The escape is the site's own non-inline exit: a
                        // pair only the engine's unifier can decide -- an
                        // attributed variable above all -- runs as a leaf
                        // builtin request with the chain OPEN, where the
                        // deopt it replaces closed the chain and left the
                        // rest of the clause to the interpreter. Measured
                        // after the hook migration, these sites WERE the
                        // remaining guard-25 population: the woken hook
                        // jumps now, and the bind that wakes it is clpr's
                        // own static V = C.
                        EmitInlineUnify(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(
                                _env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.TryGetInlineTypeTest(ins.I0, out var cbTest))
                    {
                        EmitInlineTypeTest(cbTest, ins.Pc);
                        return false;
                    }
                    if (_env.IsInlineGetAttr(ins.I0))
                    {
                        EmitInlineGetAttr(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor,
                                () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.IsInlineDomSame(ins.I0))
                    {
                        EmitInlineDomSame(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor,
                                () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.IsInlineDomEmpty(ins.I0))
                    {
                        EmitInlineDomEmpty(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor,
                                () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.IsInlineDomContains(ins.I0))
                    {
                        EmitInlineDomContains(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor,
                                () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.IsInlineDomDel(ins.I0))
                    {
                        EmitInlineDomDel(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor,
                                () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.IsInlineDomSingleton(ins.I0))
                    {
                        EmitInlineDomSingleton(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor,
                                () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.IsInlineCompare(ins.I0, out bool cbNeg))
                    {
                        EmitInlineCompare(ins.Pc, cbNeg, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor,
                                () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.IsInlineAppend(ins.I0))
                    {
                        EmitInlineAppend(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, ins.I1))));
                            StoreSlot64(WasmAbi.Cursor,
                                () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        return false;
                    }
                    if (_env.IsInlineMetaCall(ins.I0))
                    {
                        EmitInlineMetaCall(ins.Pc, SelfFid(ins), envTrim: ins.I1);
                        return true;
                    }
                    if (_env.IsInlineBarrierCall(ins.I0))
                    {
                        EmitInlineMetaCall(ins.Pc, SelfFid(ins), barrierFromX1: true,
                                           envTrim: ins.I1);
                        return true;
                    }
                    // call/N for N >= 2. Until now every one of these
                    // fell through to the deopt below, because a
                    // meta-call builtin cannot be requested through the
                    // mailbox either. maplist/3 IS call(G, X, Y), so a
                    // library built on maplist deopted once per element.
                    if (_env.IsInlineMetaCallN(ins.I0, out int appendedN))
                    {
                        EmitInlineMetaCall(ins.Pc, SelfFid(ins), envTrim: ins.I1,
                                           appended: appendedN);
                        return true;
                    }
                    EmitFlagsCheck(ins.Pc);
                    if (!_env.IsDirectBuiltin(ins.I0)) { MetaGuard(18); EmitDeopt(ins.Pc, DeoptStamped); return true; }
                    StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                        _env.EncodeBuiltinId(ins.I0, ins.I1))));
                    StoreSlot64(WasmAbi.Cursor,
                        () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                    EmitReturn(WasmVerdict.BuiltinRequest);
                    return true;
                case Opcode.ExecuteBuiltin:
                    if (_env.IsInlineUnify(ins.I0))
                    {
                        // Same escape, tail form: cursor -1, no trim.
                        EmitInlineUnify(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                                _env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.TryGetInlineTypeTest(ins.I0, out var ebTest))
                    {
                        EmitInlineTypeTest(ebTest, ins.Pc);
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineGetAttr(ins.I0))
                    {
                        EmitInlineGetAttr(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId,
                                () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineDomSame(ins.I0))
                    {
                        EmitInlineDomSame(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId,
                                () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineDomEmpty(ins.I0))
                    {
                        EmitInlineDomEmpty(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId,
                                () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineDomContains(ins.I0))
                    {
                        EmitInlineDomContains(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId,
                                () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineDomDel(ins.I0))
                    {
                        EmitInlineDomDel(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId,
                                () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineDomSingleton(ins.I0))
                    {
                        EmitInlineDomSingleton(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId,
                                () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineCompare(ins.I0, out bool ebNeg))
                    {
                        EmitInlineCompare(ins.Pc, ebNeg, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId,
                                () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineAppend(ins.I0))
                    {
                        EmitInlineAppend(ins.Pc, () =>
                        {
                            StoreSlot64(WasmAbi.BuiltinId,
                                () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                            StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                            EmitReturn(WasmVerdict.BuiltinRequest);
                        });
                        EmitProceedReturn();
                        return true;
                    }
                    if (_env.IsInlineMetaCall(ins.I0))
                    {
                        EmitInlineMetaCall(ins.Pc, 0, tail: true);
                        return true;
                    }
                    if (_env.IsInlineBarrierCall(ins.I0))
                    {
                        EmitInlineMetaCall(ins.Pc, 0, tail: true, barrierFromX1: true);
                        return true;
                    }
                    if (_env.IsInlineMetaCallN(ins.I0, out int appendedT))
                    {
                        EmitInlineMetaCall(ins.Pc, 0, tail: true,
                                           appended: appendedT);
                        return true;
                    }
                    EmitFlagsCheck(ins.Pc);
                    if (!_env.IsDirectBuiltin(ins.I0)) { MetaGuard(18); EmitDeopt(ins.Pc, DeoptStamped); return true; }
                    StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(_env.EncodeBuiltinId(ins.I0, 0))));
                    StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                    EmitReturn(WasmVerdict.BuiltinRequest);
                    return true;
                case Opcode.GetFloat: EmitGetFloat(ins); return false;
                case Opcode.PutFloat: EmitPutFloat(ins); return false;
                case Opcode.TryMeElse: EmitTryMeElse(ins); return false;
                case Opcode.RetryMeElse: EmitRetryMeElse(ins); return false;
                case Opcode.TrustMe: EmitTrustMe(ins); return false;
                case Opcode.Jump: GoTo(Bias(ins) + ins.I0); return true;
                case Opcode.AIntBin: EmitAIntBin(ins); return false;
                case Opcode.AEvalPush: EmitAEvalPush(ins); return false;
                case Opcode.AEvalBin: EmitAEvalBin(ins); return false;
                case Opcode.AEvalUn: EmitAEvalUn(ins); return false;
                case Opcode.AEvalIs: EmitAEvalIs(ins); return false;
                case Opcode.AEvalCmp: EmitAEvalCmp(ins); return false;
                case Opcode.Call: BumpGoals(); return EmitCall(ins);
                case Opcode.Execute: BumpGoals(); EmitExecute(ins); return true;
                default:
                    throw new WasmCompileException($"emit: {ins.Op} at {ins.Pc}");
            }
        }

        // ---- control ----

        /// <summary>One goal dispatched, the event time/1 counts. An i64 add
        /// on a local: the tally reaches memory once per chain, in the
        /// epilogue, not once per goal.</summary>
        private void BumpGoals()
        {
            Op(new LocalGet(LGoals));
            Op(new Int64Constant(1));
            Op(new Int64Add());
            Op(new LocalSet(LGoals));
        }

        private void EmitFlagsCheck(int pc)
        {
            LoadSlot64(WasmAbi.Flags);
            Op(new Int64Constant(0));
            Op(new Int64NotEqual());
            OpenIf();
            // Stamped like a guard although it is not one: this is the FIRST
            // thing a meta-call emits, so a meta-call that bails here leaves
            // no code and reads as anonymous. Measured, that is what half of
            // clpr's deopt sites were, and reading them as meta-call
            // declines would have sent the work to the wrong place.
            MetaGuard(17);
            EmitDeopt(pc, DeoptStamped);
            CloseNested();
        }

        private void EmitProceed(Instr ins)
        {
            EmitFlagsCheck(ins.Pc);
            EmitProceedReturn();
        }

        /// <summary>Proceed semantics: if Cp matches an in-group call's baked
        /// marker, jump straight to that caller's resume cursor -- the
        /// interpreter's marker path, done inside the module. The watermark
        /// guard hands the interpreter its GC boundary exactly where the
        /// marker path would have collected. A foreign Cp (an IL caller's
        /// marker, a bytecode address) returns the Success verdict.</summary>
        private void EmitProceedReturn()
        {
            // No shortcut for a module without return sites of its own: the
            // table can still resolve a Cp to ANOTHER module, and a plain
            // Success here would send every such return out through the host.
            //
            // The watermark guard comes FIRST and still decides everything: at
            // or past it the module owes the host a collection, so it declines
            // to resume here no matter what the table says.
            //
            // Past the guard it is one indexed read. Note this resolves MORE
            // than the old chain did: that chain only knew the return sites of
            // in-group calls, while the table knows every re-entry point of the
            // module, so a Cp that lands on one of them now resumes in wasm
            // instead of going out and coming back. Same control flow, fewer
            // crossings.
            Op(new LocalGet(LH));
            LoadSlot32(WasmAbi.HeapWatermark);
            Op(new Int32LessThanSigned());
            OpenIf();
            EmitResumeProbe(LCP);
            CloseNested();
            Op(new Int32Constant(_proceedCase));
            Op(new LocalSet(LCur));
            EmitContinueReturn();
        }

        /// <summary>A one-argument type test, answered here: deref A0 and
        /// compare its TAG. No heap, no binding, no host -- the whole builtin
        /// is a handful of comparisons, and stepping out to run it cost a
        /// chain exit each time (60% of clpr's exits, 39% of clpfd's).
        ///
        /// <para>The tag sets are the builtins' own (TypeBuiltins). The one
        /// that is easy to get wrong is VARIABLE: Ref or ATTVAR, because an
        /// attributed variable has attributes and no value -- and the
        /// libraries that call var/1 hardest are exactly the ones that make
        /// attributed variables.</para></summary>
        /// <param name="load0">Where the argument comes from. Null means X0,
        /// which is where a static call site left it. A META-CALLED goal
        /// passes a heap load instead, and must: this form hands the
        /// instruction back to the host on the paths it cannot decide, and
        /// the host re-reads the goal out of X0.</param>
        private void EmitInlineTypeTest(WasmTypeTest test, int pc, Action? load0 = null)
        {
            (load0 ?? (() => RegLoad(0)))();
            Op(new LocalSet(LC0));
            Deref();
            TagOfC0();
            Op(new LocalSet(LT0));

            void TagIsOneOf(params Tag[] tags)
            {
                for (int i = 0; i < tags.Length; i++)
                {
                    Op(new LocalGet(LT0));
                    Op(new Int32Constant((int)tags[i]));
                    Op(new Int32Equal());
                    if (i > 0) Op(new Int32Or());
                }
            }

            switch (test)
            {
                case WasmTypeTest.Var: TagIsOneOf(Tag.Ref, Tag.AttVar); break;
                case WasmTypeTest.Nonvar:
                    TagIsOneOf(Tag.Ref, Tag.AttVar);
                    Op(new Int32Constant(0)); Op(new Int32Equal());
                    break;
                case WasmTypeTest.Integer: TagIsOneOf(Tag.Int, Tag.BigInt); break;
                case WasmTypeTest.Float: TagIsOneOf(Tag.Float); break;
                case WasmTypeTest.Number:
                    TagIsOneOf(Tag.Int, Tag.BigInt, Tag.Float, Tag.Rational); break;
                case WasmTypeTest.Atom: TagIsOneOf(Tag.Atom); break;
                case WasmTypeTest.Atomic:
                    TagIsOneOf(Tag.Atom, Tag.Int, Tag.BigInt, Tag.Rational, Tag.Float); break;
                case WasmTypeTest.Compound:
                    TagIsOneOf(Tag.Str, Tag.Lis, Tag.Pstr); break;
                default:
                    throw new WasmCompileException($"type test {test} at {pc}");
            }

            Op(new Int32Constant(0));
            Op(new Int32Equal());
            OpenIf();
            GoFail();
            CloseNested();
        }

        /// <summary>Leaves 1 on the wasm stack when the cell in <paramref
        /// name="cellLocal"/> is a NON-EMPTY domain, '$fd_dom'(...), and 0 for
        /// anything else including the empty domain's atom.
        ///
        /// <para>Recognised by the functor's ATOM, read out of the staged
        /// functor table, because the arity varies with the interval count and
        /// so the functor id does too. The atom travels as a relocatable cell
        /// (the cut's rule: ids are per process, and a baked module outlives
        /// the process that built it), so the id is rebuilt from the table's
        /// packed (atom, arity) word and compared as a cell.</para>
        ///
        /// <para>Checking this is what keeps the forms a speed change: a
        /// domain is an ordinary term over a reserved functor, so anyone can
        /// write one, and answering a call about `f(1)` would be answering a
        /// call that owes a type error.</para></summary>
        private void EmitIsDomainStr(uint cellLocal)
        {
            Op(new LocalGet(cellLocal));
            Op(new Int64Constant(60));
            Op(new Int64ShiftRightUnsigned());
            Op(new Int32WrapInt64());
            Op(new Int32Constant((int)Tag.Str));
            Op(new Int32Equal());
            OpenIf(BlockType.Int32);
            {
                // heap[base] is the functor cell; its payload is the id.
                CellLoadDyn(LHeapB, LT0Of(cellLocal));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new Int32WrapInt64());
                Op(new LocalSet(LT1));                  // functor id

                // functorTable[fid] is (atomId << 32) | arity.
                LoadSlot32(WasmAbi.FunctorTableBase);
                Op(new LocalGet(LT1));
                Op(new Int32Constant(3));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new Int64Load());
                Op(new Int64Constant(32));
                Op(new Int64ShiftRightUnsigned());
                Op(new Int64Constant((long)Tag.Atom << 60));
                Op(new Int64Or());                      // the atom, as a cell
                Op(new Int64Constant(_env.AtomCell(FdDomAtomId)));
                Op(new Int64Equal());
            }
            OpenElse();
            Op(new Int32Constant(0));
            CloseNested();
        }

        /// <summary>The heap index a Str cell points at, parked in LT0 so
        /// CellLoadDyn can index from it.</summary>
        private uint LT0Of(uint cellLocal)
        {
            Op(new LocalGet(cellLocal));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT0));
            return LT0;
        }

        /// <summary>$dom_same/2, by identity and then by contents.
        ///
        /// <para>Identical cells name one domain and settle it at once, which
        /// is the common case: $dom_del hands back the cell it was given when
        /// it removes nothing, and clpfd_narrow asks '$dom_same'(New, Old)
        /// precisely to learn whether anything was removed.</para>
        ///
        /// <para>Two different domains are compared bound for bound, as
        /// CELLS. That needs no knowledge of what a bound means, so it works
        /// for inf and sup as well as for integers: equal bounds are equal
        /// cells, and a domain is canonical (ascending, disjoint,
        /// non-adjacent), so equal contents and equal arity is equality.
        /// </para>
        ///
        /// <para>Branch depths inside the loop: 0 the loop, 1 $differ,
        /// 2 $slow, 3 $done.</para></summary>
        private void EmitInlineDomSame(int pc, Action emitBuiltinExit,
                                       Action? load0 = null, Action? load1 = null)
        {
            EmitFlagsCheck(pc);
            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow

            (load0 ?? (() => RegLoad(0)))(); Op(new LocalSet(LC0)); Deref();
            Op(new LocalGet(LC0)); Op(new LocalSet(LDomA));
            (load1 ?? (() => RegLoad(1)))(); Op(new LocalSet(LC0)); Deref();
            Op(new LocalGet(LC0)); Op(new LocalSet(LDomB));

            // One domain, asked about itself.
            Op(new LocalGet(LDomA));
            Op(new LocalGet(LDomB));
            Op(new Int64Equal());
            OpenIf();
            {
                // Still has to BE a domain: a reserved functor is a term
                // anyone can write, and f(1) owes a type error.
                EmitIsDomainStr(LDomA);
                OpenIf();
                Op(new Branch(3));                          // -> $done, true
                CloseNested();
                Op(new LocalGet(LDomA));
                Op(new Int64Constant(_env.AtomCell(FdDomEmptyAtomId)));
                Op(new Int64Equal());
                OpenIf();
                Op(new Branch(3));                          // -> $done, true
                CloseNested();
                Op(new Branch(1));                          // -> $slow
            }
            CloseNested();

            // Different cells. Both must be domains for anything to be said,
            // and the empty one is an atom, so "one empty, one not" is a
            // difference this can see.
            EmitIsDomainStr(LDomA);
            Op(new LocalSet(LT2));
            EmitIsDomainStr(LDomB);
            Op(new LocalSet(LT1));

            // Neither a domain nor the empty atom: the host's.
            void NotDomainGoesSlow(uint cell, uint isDom)
            {
                Op(new LocalGet(isDom));
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                OpenIf();
                {
                    // Not a structure domain, so it has to be the empty
                    // atom. Inside the if, $slow is one block further out.
                    Op(new LocalGet(cell));
                    Op(new Int64Constant(_env.AtomCell(FdDomEmptyAtomId)));
                    Op(new Int64NotEqual());
                    Op(new BranchIf(1));                    // -> $slow
                }
                CloseNested();
            }
            NotDomainGoesSlow(LDomA, LT2);
            NotDomainGoesSlow(LDomB, LT1);

            // Exactly one of them empty: different, and no walk needed.
            Op(new LocalGet(LT2));
            Op(new LocalGet(LT1));
            Op(new Int32NotEqual());
            OpenIf();
            GoFail();
            CloseNested();

            // Both empty was the identical case above, so both are
            // structures here. Arity first.
            Op(new LocalGet(LDomA));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT0));
            Op(new LocalGet(LDomB));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LDomBase2));

            CellLoadDyn(LHeapB, LT0);
            Op(new LocalSet(LC1));
            CellLoadDyn(LHeapB, LDomBase2);
            Op(new LocalSet(LC2));
            Op(new LocalGet(LC1));
            Op(new LocalGet(LC2));
            Op(new Int64NotEqual());
            OpenIf();
            GoFail();                                       // different functor
            CloseNested();

            // Same functor means the same arity; read it once.
            Op(new LocalGet(LC1));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));
            LoadSlot32(WasmAbi.FunctorTableBase);
            Op(new LocalGet(LT1));
            Op(new Int32Constant(3));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new Int64Load());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));                          // arity

            Op(new Int32Constant(1));
            Op(new LocalSet(LT2));                          // argument index

            OpenBlock();                                    // $differ
            OpenLoop();                                     // $cmp
            {
                Op(new LocalGet(LT2));
                Op(new LocalGet(LT1));
                Op(new Int32GreaterThanSigned());
                Op(new BranchIf(3));                        // all equal -> $done

                // Offset 1: the bases point AT the functor cell, and the
                // arguments start after it. Loading at the base compares the
                // functors (already equal) and walks off the end one short,
                // so two domains differing only in their last bound would
                // read as equal.
                CellLoadDyn(LHeapB, LT0, 1);
                Op(new LocalSet(LC1));
                CellLoadDyn(LHeapB, LDomBase2, 1);
                Op(new LocalSet(LC2));
                Op(new LocalGet(LC1));
                Op(new LocalGet(LC2));
                Op(new Int64NotEqual());
                Op(new BranchIf(1));                        // -> $differ

                Op(new LocalGet(LT2));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new LocalSet(LT2));
                // The bases move with the cursor so CellLoadDyn stays put.
                Op(new LocalGet(LT0));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new LocalSet(LT0));
                Op(new LocalGet(LDomBase2));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new LocalSet(LDomBase2));
                Op(new Branch(0));                          // -> $cmp
            }
            CloseNested();                                  // $cmp
            CloseNested();                                  // $differ
            GoFail();

            CloseNested();                                  // $slow
            emitBuiltinExit();
            CloseNested();                                  // $done
        }

        /// <summary>$dom_empty/1: the empty domain is an atom of its own, so
        /// the test is a cell comparison. A non-empty domain answers FALSE
        /// here rather than stepping aside, which is the half that matters --
        /// clpfd_narrow asks this on the path where the domain did change,
        /// and the answer is almost always no.</summary>
        private void EmitInlineDomEmpty(int pc, Action emitBuiltinExit,
                                        Action? load0 = null)
        {
            EmitFlagsCheck(pc);
            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow

            (load0 ?? (() => RegLoad(0)))(); Op(new LocalSet(LC0)); Deref();
            Op(new LocalGet(LC0)); Op(new LocalSet(LDomA));

            Op(new LocalGet(LDomA));
            Op(new Int64Constant(_env.AtomCell(FdDomEmptyAtomId)));
            Op(new Int64Equal());
            OpenIf();
            Op(new Branch(2));                              // empty -> $done, true
            CloseNested();

            EmitIsDomainStr(LDomA);
            OpenIf();
            GoFail();                                       // a domain, not empty
            CloseNested();

            CloseNested();                                  // $slow
            emitBuiltinExit();
            CloseNested();                                  // $done
        }

        /// <summary>Walks a domain's intervals looking for an integer.
        ///
        /// <para>Emitted INSIDE the $done/$slow pair the forms open, and it
        /// opens two more of its own, so from inside the loop the branch
        /// depths are: 0 the loop, 1 $found, 2 $absent, 3 $slow, 4 $done.
        /// Getting one of those wrong is a jump to the wrong arm, which is a
        /// wrong answer and not a crash, so they are written down.</para>
        ///
        /// <para>Every bound it compares must be an INTEGER. inf and sup are
        /// atoms (ADR-051 D3) and an unbounded domain steps aside instead:
        /// the comparison would have to be three-way, and the domains this
        /// runs on are finite. Measured on queens_fd, no unbounded domain
        /// reaches it.</para>
        ///
        /// <para>The callbacks run OUTSIDE the loop, where the depths are
        /// different again: in onFound they are 0 $absent, 1 $slow, 2 $done,
        /// and in onAbsent 0 $slow, 1 $done. Counting them as though $found
        /// were still open sends a found value down the absent arm, which
        /// answers the wrong thing and answers it quietly.</para>
        ///
        /// <para>Expects: LT0 the structure's heap index, LT1 its arity,
        /// LDomB the value. Spends LT2 as the cursor and LC1 as scratch
        /// (Deref spends LC1 too, so nothing is derefed inside).</para>
        /// </summary>
        private void EmitDomIntervalScan(Action onFound, Action onAbsent)
        {
            Op(new Int32Constant(0));
            Op(new LocalSet(LT2));                          // cursor

            OpenBlock();                                    // $absent
            OpenBlock();                                    // $found
            OpenLoop();                                     // $probe
            {
                // Past the last bound: not in any interval.
                Op(new LocalGet(LT2));
                Op(new LocalGet(LT1));
                Op(new Int32GreaterThanOrEqualSigned());
                Op(new BranchIf(2));                        // -> $absent

                // lo = heap[base + 1 + i], an integer or this is not ours.
                CellLoadDyn(LHeapB, LT0, 1);
                Op(new LocalSet(LC1));
                Op(new LocalGet(LC1));
                Op(new Int64Constant(60));
                Op(new Int64ShiftRightUnsigned());
                Op(new Int32WrapInt64());
                Op(new Int32Constant((int)Tag.Int));
                Op(new Int32NotEqual());
                Op(new BranchIf(3));                        // -> $slow

                // V < lo: the intervals ascend, so nothing further can hold it.
                Op(new LocalGet(LDomB));
                Op(new LocalGet(LC1));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                EmitSignExtend60();
                Op(new Int64LessThanSigned());
                Op(new BranchIf(2));                        // -> $absent

                // hi = heap[base + 2 + i].
                CellLoadDyn(LHeapB, LT0, 2);
                Op(new LocalSet(LC1));
                Op(new LocalGet(LC1));
                Op(new Int64Constant(60));
                Op(new Int64ShiftRightUnsigned());
                Op(new Int32WrapInt64());
                Op(new Int32Constant((int)Tag.Int));
                Op(new Int32NotEqual());
                Op(new BranchIf(3));                        // -> $slow

                // V <= hi: inside this interval.
                Op(new LocalGet(LDomB));
                Op(new LocalGet(LC1));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                EmitSignExtend60();
                Op(new Int64LessThanOrEqualSigned());
                Op(new BranchIf(1));                        // -> $found

                // Next interval: the cursor counts bounds, and the base
                // moves with it so CellLoadDyn's +1/+2 stay put.
                Op(new LocalGet(LT2));
                Op(new Int32Constant(2));
                Op(new Int32Add());
                Op(new LocalSet(LT2));
                Op(new LocalGet(LT0));
                Op(new Int32Constant(2));
                Op(new Int32Add());
                Op(new LocalSet(LT0));
                Op(new Branch(0));                          // -> $probe
            }
            CloseNested();                                  // $probe
            CloseNested();                                  // $found
            onFound();
            CloseNested();                                  // $absent
            onAbsent();
        }

        /// <summary>Sign-extends a 60-bit payload on the stack to i64: an
        /// Int cell keeps its value in the payload, and a negative bound read
        /// as unsigned compares wrong against everything.</summary>
        private void EmitSignExtend60()
        {
            Op(new Int64Constant(4));
            Op(new Int64ShiftLeft());
            Op(new Int64Constant(4));
            Op(new Int64ShiftRightSigned());
        }

        /// <summary>Loads the domain in <paramref name="load"/> and its value
        /// argument, leaving LT0 at the structure, LT1 at the arity and LDomB
        /// at the value. Branches to $slow for anything that is not a
        /// non-empty domain and an integer.</summary>
        private void EmitDomAndIntSetup(Action load, Action loadValue)
        {
            load(); Op(new LocalSet(LC0)); Deref();
            Op(new LocalGet(LC0)); Op(new LocalSet(LDomA));
            EmitIsDomainStr(LDomA);
            Op(new Int32Constant(0));
            Op(new Int32Equal());
            Op(new BranchIf(0));                            // not a domain -> $slow

            loadValue(); Op(new LocalSet(LC0)); Deref();
            TagOfC0();
            Op(new Int32Constant((int)Tag.Int));
            Op(new Int32NotEqual());
            Op(new BranchIf(0));                            // -> $slow
            Op(new LocalGet(LC0));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            EmitSignExtend60();
            Op(new LocalSet(LDomB));                        // the value

            // The structure's heap index, and its arity from the table.
            Op(new LocalGet(LDomA));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT0));
            CellLoadDyn(LHeapB, LT0);
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));
            LoadSlot32(WasmAbi.FunctorTableBase);
            Op(new LocalGet(LT1));
            Op(new Int32Constant(3));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new Int64Load());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));                          // arity
        }

        /// <summary>$dom_singleton(+Dom, -V): one interval whose bounds are
        /// the same value. No walk needed, so this only wants the arity and
        /// the first two bounds.
        ///
        /// <para>clpfd_narrow asks it right after $dom_same says the domain
        /// changed, which is why it is worth answering even though the answer
        /// is usually no.</para></summary>
        private void EmitInlineDomSingleton(int pc, Action emitBuiltinExit,
                                            Action? load0 = null, Action? load1 = null)
        {
            EmitFlagsCheck(pc);
            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow

            (load0 ?? (() => RegLoad(0)))(); Op(new LocalSet(LC0)); Deref();
            Op(new LocalGet(LC0)); Op(new LocalSet(LDomA));

            // The empty domain is no singleton, and saying so needs nothing.
            Op(new LocalGet(LDomA));
            Op(new Int64Constant(_env.AtomCell(FdDomEmptyAtomId)));
            Op(new Int64Equal());
            OpenIf();
            GoFail();
            CloseNested();

            EmitIsDomainStr(LDomA);
            Op(new Int32Constant(0));
            Op(new Int32Equal());
            Op(new BranchIf(0));                            // not a domain -> $slow

            Op(new LocalGet(LDomA));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT0));                          // the structure
            CellLoadDyn(LHeapB, LT0);
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));
            LoadSlot32(WasmAbi.FunctorTableBase);
            Op(new LocalGet(LT1));
            Op(new Int32Constant(3));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new Int64Load());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));                          // arity

            // More than one interval: not a singleton.
            Op(new LocalGet(LT1));
            Op(new Int32Constant(2));
            Op(new Int32NotEqual());
            OpenIf();
            GoFail();
            CloseNested();

            CellLoadDyn(LHeapB, LT0, 1);
            Op(new LocalSet(LC1));
            CellLoadDyn(LHeapB, LT0, 2);
            Op(new LocalSet(LC2));

            // Both bounds integers, or this is an unbounded domain and the
            // host's business.
            Op(new LocalGet(LC1));
            Op(new Int64Constant(60));
            Op(new Int64ShiftRightUnsigned());
            Op(new Int32WrapInt64());
            Op(new Int32Constant((int)Tag.Int));
            Op(new Int32NotEqual());
            Op(new BranchIf(0));                            // -> $slow
            Op(new LocalGet(LC2));
            Op(new Int64Constant(60));
            Op(new Int64ShiftRightUnsigned());
            Op(new Int32WrapInt64());
            Op(new Int32Constant((int)Tag.Int));
            Op(new Int32NotEqual());
            Op(new BranchIf(0));                            // -> $slow

            // lo != hi: an interval, not a point.
            Op(new LocalGet(LC1));
            Op(new LocalGet(LC2));
            Op(new Int64NotEqual());
            OpenIf();
            GoFail();
            CloseNested();

            // The bound moves to a local of this form's own BEFORE the
            // unification: EmitUnifyTwo derefs its other operand, Deref
            // spends LC1, and the value would be read back as whatever the
            // deref left there. (Measured: the answers changed and clpfd
            // took longer paths, with nothing failing outright.)
            Op(new LocalGet(LC1));
            Op(new LocalSet(LDomB));
            EmitUnifyTwo(load1 ?? (() => RegLoad(1)),
                         () => Op(new LocalGet(LDomB)), pc);
            Op(new Branch(1));                              // -> $done

            CloseNested();                                  // $slow
            emitBuiltinExit();
            CloseNested();                                  // $done
        }

        /// <summary>$dom_contains(+Dom, +V): the walk, and nothing else.
        /// </summary>
        private void EmitInlineDomContains(int pc, Action emitBuiltinExit,
                                           Action? load0 = null, Action? load1 = null)
        {
            EmitFlagsCheck(pc);
            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow
            EmitDomAndIntSetup(load0 ?? (() => RegLoad(0)),
                               load1 ?? (() => RegLoad(1)));
            EmitDomIntervalScan(
                onFound: () => Op(new Branch(2)),           // -> $done, true
                onAbsent: GoFail);
            CloseNested();                                  // $slow
            emitBuiltinExit();
            CloseNested();                                  // $done
        }

        /// <summary>Writes the rebuilt domain and unifies it. The functor is
        /// in LC0, the resulting arity in LDomI, and the source is LDomBase2
        /// (the structure) with LT2 the offset of the low bound of the
        /// interval being changed.
        ///
        /// <para>Three spans: the bounds before the interval, what replaces
        /// it (nothing when it disappears, four bounds when it splits), and
        /// the bounds after. Written in that order into fresh heap.</para>
        /// </summary>
        private void EmitDomRebuildBody(int pc, int deltaIntervals, Action? load2)
        {
            // Room for the functor and every bound, before anything is
            // written. The count is a run-time value, so this is the dynamic
            // form of EmitHeapGuard.
            Op(new LocalGet(LH));
            Op(new LocalGet(LDomI));
            Op(new Int32Add());
            LoadSlot32(WasmAbi.HeapWatermark);
            Op(new Int32GreaterThanOrEqualSigned());
            OpenIf();
            EmitDeopt(pc, 19);
            CloseNested();

            Op(new LocalGet(LH));
            Op(new LocalSet(LT0Alt));                       // the new structure

            CellStoreDyn(LHeapB, LH, 0, () => Op(new LocalGet(LC0)));
            Op(new LocalGet(LH));
            Op(new Int32Constant(1));
            Op(new Int32Add());
            Op(new LocalSet(LH));

            // The bounds before the interval: source indices 1 .. LT2.
            Op(new Int32Constant(1));
            Op(new LocalSet(LDomJ));
            OpenBlock();
            OpenLoop();
            {
                Op(new LocalGet(LDomJ));
                Op(new LocalGet(LT2));
                Op(new Int32GreaterThanSigned());
                Op(new BranchIf(1));
                EmitCopyBound();
                Op(new Branch(0));
            }
            CloseNested();
            CloseNested();

            if (deltaIntervals > 0)
            {
                // lo, V - 1, V + 1, hi.
                EmitStoreBound(() => Op(new LocalGet(LC1)));
                EmitStoreBound(() =>
                {
                    Op(new LocalGet(LDomB));
                    Op(new Int64Constant(1));
                    Op(new Int64Subtract());
                    EmitIntCellFromI64();
                });
                EmitStoreBound(() =>
                {
                    Op(new LocalGet(LDomB));
                    Op(new Int64Constant(1));
                    Op(new Int64Add());
                    EmitIntCellFromI64();
                });
                EmitStoreBound(() => Op(new LocalGet(LC2)));
            }

            // The bounds after it: source indices LT2 + 3 .. arity.
            Op(new LocalGet(LT2));
            Op(new Int32Constant(3));
            Op(new Int32Add());
            Op(new LocalSet(LDomJ));
            OpenBlock();
            OpenLoop();
            {
                Op(new LocalGet(LDomJ));
                Op(new LocalGet(LT1));
                Op(new Int32GreaterThanSigned());
                Op(new BranchIf(1));
                EmitCopyBound();
                Op(new Branch(0));
            }
            CloseNested();
            CloseNested();

            EmitUnifyTwo(load2 ?? (() => RegLoad(2)), () =>
            {
                Op(new LocalGet(LT0Alt));
                Op(new Int64ExtendInt32Unsigned());
                Op(new Int64Constant((long)Tag.Str << 60));
                Op(new Int64Or());
            }, pc);
        }

        /// <summary>heap[LH++] = heap[LDomBase2 + LDomJ++], the copy step both
        /// spans share.</summary>
        private void EmitCopyBound()
        {
            Op(new LocalGet(LHeapB));
            Op(new LocalGet(LDomBase2));
            Op(new LocalGet(LDomJ));
            Op(new Int32Add());
            Op(new Int32Constant(3));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new Int64Load());
            Op(new LocalSet(LC0));
            CellStoreDyn(LHeapB, LH, 0, () => Op(new LocalGet(LC0)));
            Op(new LocalGet(LH));
            Op(new Int32Constant(1));
            Op(new Int32Add());
            Op(new LocalSet(LH));
            Op(new LocalGet(LDomJ));
            Op(new Int32Constant(1));
            Op(new Int32Add());
            Op(new LocalSet(LDomJ));
        }

        /// <summary>heap[LH++] = the cell the callback pushes.</summary>
        private void EmitStoreBound(Action cell)
        {
            CellStoreDyn(LHeapB, LH, 0, cell);
            Op(new LocalGet(LH));
            Op(new Int32Constant(1));
            Op(new Int32Add());
            Op(new LocalSet(LH));
        }

        /// <summary>An i64 value on the stack becomes an Int cell.</summary>
        private void EmitIntCellFromI64()
        {
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int64Constant((long)Tag.Int << 60));
            Op(new Int64Or());
        }

        /// <summary>Rebuilds a domain whose interval COUNT changed, and
        /// unifies it with the output argument.
        ///
        /// <para><paramref name="deltaIntervals"/> is -1 when the interval
        /// held only the value being removed and disappears, and +1 when the
        /// value is strictly inside and splits it. The functor comes from the
        /// table the host stages, indexed by the resulting count, because
        /// interning one is not something a module can do. A count the table
        /// does not reach reads as zero and exits to the host, which is the
        /// same answer it gave before the table existed.</para>
        ///
        /// <para>Expects the scan's state: LT0 at the pair holding the value,
        /// LT2 the offset of its low bound, LT1 the arity, LDomB the value,
        /// LDomBase2 walked to the pair as well, LC1 and LC2 the two bounds.
        /// Emitted inside the found arm, where $slow is one block out.</para>
        /// </summary>
        private void EmitDomRebuild(int pc, int deltaIntervals, Action? load2)
        {
            // The resulting arity, and the functor for it.
            Op(new LocalGet(LT1));
            Op(new Int32Constant(2 * deltaIntervals));
            Op(new Int32Add());
            Op(new LocalSet(LDomI));                        // new arity

            // An empty result is the empty ATOM, not a zero-arity structure.
            Op(new LocalGet(LDomI));
            Op(new Int32Constant(0));
            Op(new Int32LessThanOrEqualSigned());
            OpenIf();
            {
                EmitUnifyTwo(load2 ?? (() => RegLoad(2)),
                             () => Op(new Int64Constant(
                                 _env.AtomCell(FdDomEmptyAtomId))), pc);
            }
            OpenElse();
            {
                // functorCells[newArity / 2], zero when the table stops short.
                LoadSlot32(WasmAbi.FdDomFunctorBase);
                Op(new LocalSet(LT0Alt));
                Op(new LocalGet(LDomI));
                Op(new Int32Constant(1));
                Op(new Int32ShiftRightSigned());
                Op(new LocalSet(LDomJ));                    // interval count
                Op(new LocalGet(LDomJ));
                LoadSlot32(WasmAbi.FdDomFunctorLength);
                Op(new Int32GreaterThanOrEqualSigned());
                // Depths here: 0 this else, 1 the found arm's if, 2 $absent,
                // 3 $slow, 4 $done. Landing on $absent instead would unify
                // the output with the domain that came IN, which is a wrong
                // answer and not a slower one.
                Op(new BranchIf(3));                        // past it -> $slow

                Op(new LocalGet(LT0Alt));
                Op(new LocalGet(LDomJ));
                Op(new Int32Constant(3));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new Int64Load());
                Op(new LocalSet(LC0));                      // the functor cell
                Op(new LocalGet(LC0));
                Op(new Int64Constant(0));
                Op(new Int64Equal());
                Op(new BranchIf(3));                        // unfilled -> $slow

                EmitDomRebuildBody(pc, deltaIntervals, load2);
            }
            CloseNested();
        }

        /// <summary>$dom_del(+Dom, +V, -Out), answered in the module when it
        /// can be.
        ///
        /// <para>The value is not there: Out is the domain that came in, the
        /// same cell, no allocation. Most removals are this, because clpfd
        /// posts a disequality by removing a value and asking whether the
        /// domain changed, and by the time a propagator re-fires the value is
        /// usually gone already. Measured on queens_fd(7), 7,581 of
        /// 8,807.</para>
        ///
        /// <para>The value is a bound of an interval wider than one: the
        /// interval narrows, the interval COUNT does not change, and the
        /// rebuilt domain reuses the functor that is already on the heap.
        /// That is the largest of the removals that do remove (503 of 1,226
        /// on queens_fd(7), 63% on send+more=money).</para>
        ///
        /// <para>The value splits an interval or empties one: the count
        /// changes, so the functor changes, and the module cannot intern a
        /// functor. Those step aside.</para></summary>
        private void EmitInlineDomDel(int pc, Action emitBuiltinExit,
                                      Action? load0 = null, Action? load1 = null,
                                      Action? load2 = null)
        {
            EmitFlagsCheck(pc);
            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow
            EmitDomAndIntSetup(load0 ?? (() => RegLoad(0)),
                               load1 ?? (() => RegLoad(1)));

            // The scan moves LT0 along, so the structure's own index is kept
            // here for the copy.
            Op(new LocalGet(LT0));
            Op(new LocalSet(LDomBase2));

            EmitDomIntervalScan(
                onFound: () =>
                {
                    // The interval that holds it. LT0 points at its pair and
                    // LT2 is the offset of its low bound among the bounds.
                    CellLoadDyn(LHeapB, LT0, 1);
                    Op(new LocalSet(LC1));                  // lo cell
                    CellLoadDyn(LHeapB, LT0, 2);
                    Op(new LocalSet(LC2));                  // hi cell

                    // Three shapes, and which one decides the interval
                    // COUNT of the result: a one-value interval disappears
                    // (count - 1), a value strictly inside splits it
                    // (count + 1), and a value at a bound narrows it (count
                    // unchanged). Only the last reuses the functor on the
                    // heap; the other two look theirs up by count.
                    Op(new LocalGet(LC1));
                    Op(new LocalGet(LC2));
                    Op(new Int64Equal());
                    OpenIf();
                    {
                        EmitDomRebuild(pc, -1, load2);
                        Op(new Branch(3));                  // -> $done
                    }
                    CloseNested();

                    Op(new LocalGet(LDomB));
                    Op(new LocalGet(LC1));
                    Op(new Int64Constant(Cell.PayloadMask));
                    Op(new Int64And());
                    EmitSignExtend60();
                    Op(new Int64NotEqual());
                    Op(new LocalGet(LDomB));
                    Op(new LocalGet(LC2));
                    Op(new Int64Constant(Cell.PayloadMask));
                    Op(new Int64And());
                    EmitSignExtend60();
                    Op(new Int64NotEqual());
                    Op(new Int32And());
                    OpenIf();
                    {
                        EmitDomRebuild(pc, +1, load2);
                        Op(new Branch(3));                  // -> $done
                    }
                    CloseNested();

                    // A bound moves in. Room for the functor and the bounds,
                    // checked before anything is written: the count is a
                    // runtime value, so this is the dynamic form of the
                    // guard EmitHeapGuard does for a constant.
                    Op(new LocalGet(LH));
                    Op(new LocalGet(LT1));
                    Op(new Int32Add());
                    LoadSlot32(WasmAbi.HeapWatermark);
                    Op(new Int32GreaterThanOrEqualSigned());
                    OpenIf();
                    EmitDeopt(pc, 19);
                    CloseNested();

                    // Copy the structure whole, functor included, and then
                    // correct the one bound. Simpler than copying around the
                    // hole, and the same number of stores.
                    Op(new Int32Constant(0));
                    Op(new LocalSet(LDomI));
                    OpenBlock();                            // $copied
                    OpenLoop();                             // $copy
                    {
                        Op(new LocalGet(LDomI));
                        Op(new LocalGet(LT1));
                        Op(new Int32GreaterThanSigned());
                        Op(new BranchIf(1));                // -> $copied

                        CellLoadDyn(LHeapB, LDomBase2);
                        Op(new LocalSet(LC0));
                        CellStoreDyn(LHeapB, LH, 0, () => Op(new LocalGet(LC0)));

                        Op(new LocalGet(LDomBase2));
                        Op(new Int32Constant(1));
                        Op(new Int32Add());
                        Op(new LocalSet(LDomBase2));
                        Op(new LocalGet(LH));
                        Op(new Int32Constant(1));
                        Op(new Int32Add());
                        Op(new LocalSet(LH));
                        Op(new LocalGet(LDomI));
                        Op(new Int32Constant(1));
                        Op(new Int32Add());
                        Op(new LocalSet(LDomI));
                        Op(new Branch(0));                  // -> $copy
                    }
                    CloseNested();                          // $copy
                    CloseNested();                          // $copied

                    // LH now points past the copy; the structure starts
                    // arity + 1 cells back.
                    Op(new LocalGet(LH));
                    Op(new LocalGet(LT1));
                    Op(new Int32Subtract());
                    Op(new Int32Constant(1));
                    Op(new Int32Subtract());
                    Op(new LocalSet(LDomI));                // the new structure

                    // V == lo: the low bound becomes V + 1. Otherwise V == hi
                    // and the high bound becomes V - 1.
                    Op(new LocalGet(LDomB));
                    Op(new LocalGet(LC1));
                    Op(new Int64Constant(Cell.PayloadMask));
                    Op(new Int64And());
                    EmitSignExtend60();
                    Op(new Int64Equal());
                    OpenIf();
                    {
                        Op(new LocalGet(LT2));
                        Op(new Int32Constant(1));
                        Op(new Int32Add());
                        Op(new LocalGet(LDomI));
                        Op(new Int32Add());
                        Op(new LocalSet(LDa));
                        CellStoreDyn(LHeapB, LDa, 0, () =>
                        {
                            Op(new LocalGet(LDomB));
                            Op(new Int64Constant(1));
                            Op(new Int64Add());
                            Op(new Int64Constant(Cell.PayloadMask));
                            Op(new Int64And());
                            Op(new Int64Constant((long)Tag.Int << 60));
                            Op(new Int64Or());
                        });
                    }
                    OpenElse();
                    {
                        Op(new LocalGet(LT2));
                        Op(new Int32Constant(2));
                        Op(new Int32Add());
                        Op(new LocalGet(LDomI));
                        Op(new Int32Add());
                        Op(new LocalSet(LDa));
                        CellStoreDyn(LHeapB, LDa, 0, () =>
                        {
                            Op(new LocalGet(LDomB));
                            Op(new Int64Constant(1));
                            Op(new Int64Subtract());
                            Op(new Int64Constant(Cell.PayloadMask));
                            Op(new Int64And());
                            Op(new Int64Constant((long)Tag.Int << 60));
                            Op(new Int64Or());
                        });
                    }
                    CloseNested();

                    EmitUnifyTwo(load2 ?? (() => RegLoad(2)), () =>
                    {
                        Op(new LocalGet(LDomI));
                        Op(new Int64ExtendInt32Unsigned());
                        Op(new Int64Constant((long)Tag.Str << 60));
                        Op(new Int64Or());
                    }, pc);
                    Op(new Branch(2));                      // -> $done
                },
                onAbsent: () =>
                {
                    // Out is the domain that came in, cell for cell.
                    EmitUnifyTwo(load2 ?? (() => RegLoad(2)),
                                 () => Op(new LocalGet(LDomA)), pc);
                    Op(new Branch(1));                      // -> $done
                });
            CloseNested();                                  // $slow
            emitBuiltinExit();
            CloseNested();                                  // $done
        }

        private bool EmitCall(Instr ins)
        {
            if (!_callee.TryGetValue(ins.Pc, out int callee))
                throw new WasmCompileException($"call at {ins.Pc} has no call site");
            if (_env.TryGetBuiltin(callee, out int builtinId))
            {
                if (_env.IsInlineUnify(builtinId))
                {
                    EmitInlineUnify(ins.Pc);
                    return false;               // falls through to the next goal
                }
                if (_env.TryGetInlineTypeTest(builtinId, out var typeTest))
                {
                    EmitInlineTypeTest(typeTest, ins.Pc);
                    return false;               // falls through to the next goal
                }
                if (_env.IsInlineGetAttr(builtinId))
                {
                    EmitInlineGetAttr(ins.Pc, () =>
                    {
                        StoreSlot64(WasmAbi.BuiltinId,
                            () => Op(new Int64Constant(_env.EncodeBuiltinId(builtinId, 0))));
                        StoreSlot64(WasmAbi.Cursor,
                            () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                        EmitReturn(WasmVerdict.BuiltinRequest);
                    });
                    return false;
                }
                if (_env.IsInlineDomSame(builtinId))
                {
                    EmitInlineDomSame(ins.Pc, () =>
                    {
                        StoreSlot64(WasmAbi.BuiltinId,
                            () => Op(new Int64Constant(_env.EncodeBuiltinId(builtinId, 0))));
                        StoreSlot64(WasmAbi.Cursor,
                            () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                        EmitReturn(WasmVerdict.BuiltinRequest);
                    });
                    return false;
                }
                if (_env.IsInlineCompare(builtinId, out bool cNeg))
                {
                    EmitInlineCompare(ins.Pc, cNeg, () =>
                    {
                        StoreSlot64(WasmAbi.BuiltinId,
                            () => Op(new Int64Constant(_env.EncodeBuiltinId(builtinId, 0))));
                        StoreSlot64(WasmAbi.Cursor,
                            () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                        EmitReturn(WasmVerdict.BuiltinRequest);
                    });
                    return false;               // falls through to the next goal
                }
                // The builtin runs on the host: leave its id and the return
                // cursor in the mailbox and step out (env trimming skipped;
                // a CP the builtin pushes just sits a little higher).
                EmitFlagsCheck(ins.Pc);
                StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(_env.EncodeBuiltinId(builtinId, 0))));
                StoreSlot64(WasmAbi.Cursor,
                    () => Op(new Int64Constant(_env.EncodeAddress(ins.Pc + 9))));
                EmitReturn(WasmVerdict.BuiltinRequest);
                return true;
            }
            EmitFlagsCheck(ins.Pc);
            // Env trimming is skipped: frames sit a little higher, results
            // are unaffected. CP becomes the marker that re-enters us at the
            // return cursor. The callee enters a new procedure: refresh its
            // cut barrier (the interpreter's SetB0(B) before every call).
            StoreSlotFromI32Local(WasmAbi.CutBarrier, LB);
            Op(new Int32Constant(_env.EncodeReturnMarker(SelfFid(ins), ins.Pc + 9)));
            Op(new LocalSet(LCP));
            if (_entryByFid.TryGetValue(callee, out int calleeEntry))
            {
                // In-group non-tail call: jump straight to the entry; the
                // proceed will match the Cp marker just staged and jump back
                // to our resume cursor -- no host round-trip. The watermark
                // guard gives the interpreter its GC boundary exactly where
                // the marker path would have taken it. The jump never reaches
                // code the table has left behind: when a member leaves its
                // module (eviction, takeover) the registry evicts its baked
                // callers with it (WasmModuleRegistry), so this member is
                // gone before the callee is.
                Op(new LocalGet(LH));
                LoadSlot32(WasmAbi.HeapWatermark);
                Op(new Int32GreaterThanOrEqualSigned());
                OpenIf();
                EmitDeopt(ins.Pc, 19);
                CloseNested();
                GoTo(calleeEntry);
                return true;
            }
            // Not in this module. Try to reach it through the table before
            // giving up to the host: EncodeCallTarget is marker(callee, 0),
            // which is exactly the callee's fresh-entry row.
            EmitForeignCallOrExit(callee, ins.Pc);
            return true;
        }

        /// <summary>A call whose callee lives in ANOTHER module: resolve it
        /// through the resume table and tail-call it in wasm, and only fall
        /// back to the host verdict when it cannot be reached from here (a
        /// module this thread has not registered, or one that is not on the
        /// tier at all).
        ///
        /// <para>The watermark guard comes first for the same reason the
        /// in-group call has one: at or past it the host owes a collection,
        /// and staying inside wasm would skip the boundary where it happens.
        /// </para></summary>
        private void EmitForeignCallOrExit(int callee, int pc)
        {
            int marker = _env.EncodeCallTarget(callee);
            Op(new LocalGet(LH));
            LoadSlot32(WasmAbi.HeapWatermark);
            Op(new Int32LessThanSigned());
            OpenIf();
            {
                Op(new Int32Constant(marker));
                Op(new LocalSet(LT1));
                EmitResumeProbe(LT1);
            }
            CloseNested();
            StoreSlot64(WasmAbi.Pc, () => Op(new Int64Constant(marker)));
            EmitReturn(WasmVerdict.SuccessTailCall);
        }

        /// <summary>Whether the clause holding <paramref name="pc"/> has an
        /// ENVIRONMENT FRAME, scanning back to the clause's start.
        ///
        /// <para>It decides whether an inline meta-call may write CP. A
        /// normal non-tail call writes CP freely BECAUSE the frame holds the
        /// clause's own continuation and Deallocate restores it. A clause
        /// whose only call is a BUILTIN has no frame, and needs none: a
        /// builtin never touches CP, execution just falls through to the next
        /// instruction. Turning such a site into a predicate call and writing
        /// CP there leaves nothing to restore -- the clause's Proceed then
        /// reads the marker it was handed and jumps back to itself, forever.
        /// That is not hypothetical: it is $wake_call/1, whose whole body is
        /// call(G), spinning 10,000,001 dispatches on its own resume.</para>
        ///
        /// <para>Answers FALSE when unsure. The caller's response to false is
        /// to build a frame of its own, which is correct either way and only
        /// costs two instructions -- so the conservative direction is the one
        /// that over-allocates, never the one that writes CP into thin
        /// air.</para></summary>
        private bool ClauseHasFrame(int pc)
        {
            if (!_byPc.TryGetValue(pc, out int i)) return false;
            int section = _instrs[i].Section;
            for (int k = i - 1; k >= 0 && _instrs[k].Section == section; k--)
            {
                switch (_instrs[k].Op)
                {
                    case Opcode.Allocate:
                    case Opcode.AllocateGetLevel:
                        return true;
                    // A clause boundary: anything before it belongs to
                    // another clause and says nothing about this one.
                    case Opcode.TryMeElse:
                    case Opcode.RetryMeElse:
                    case Opcode.TrustMe:
                    case Opcode.Try:
                    case Opcode.Retry:
                    case Opcode.Trust:
                        return false;
                }
            }
            return false;
        }

        /// <summary>append/3's DETERMINISTIC mode, built in the module.
        ///
        /// <para>It is the largest single source of builtin exits measured:
        /// 2,000 of clpr's 5,000, twice the next one. And the exit is what
        /// costs -- the builtins of a whole run do 63 ms of work while
        /// getting to them and back costs 271 ms, so a builtin that leaves
        /// the module pays about four times its own weight.</para>
        ///
        /// <para>Two passes, exactly as the host does it: walk the spine to
        /// count, claim 2N+1 cells in one go, walk again writing the pairs.
        /// The layout is the host's too -- pair i's LIS cell at start + 2i
        /// points at its head at start + 2i + 1, and the cell after a head is
        /// the next pair's LIS slot (ADR-017) -- because the result is an
        /// ordinary list that everything else reads.</para>
        ///
        /// <para>Anything that is not a plain spine ending in [] steps out
        /// rather than being decided here: an unbound tail is append/3's
        /// other mode, which enumerates splits off a choice point; a PSTR is
        /// a list this walk cannot follow; an improper tail is a failure the
        /// host already knows how to produce. Declining is always correct,
        /// and it is what the measured clpr corpus never needs -- every one
        /// of its calls is (+, +, -).</para></summary>
        private void EmitInlineAppend(int pc, Action emitBuiltinExit)
        {
            EmitFlagsCheck(pc);
            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow

            // ---- pass one: how long is L1, and what ends it ----
            RegLoad(0); Op(new LocalSet(LC0)); Deref();
            Op(new Int32Constant(0)); Op(new LocalSet(LAtSlot));     // count
            OpenBlock();                                    // $counted
            OpenLoop();                                     // $count
            {
                TagOfC0();
                Op(new Int32Constant((int)Tag.Lis));
                Op(new Int32NotEqual());
                Op(new BranchIf(1));                        // -> $counted
                // A LIS cell's payload is its HEAD index; the tail is the
                // cell right after the head.
                Op(new LocalGet(LC0));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new Int32WrapInt64());
                Op(new LocalSet(LT0));
                CellLoadDyn(LHeapB, LT0, 1);
                Op(new LocalSet(LC0));
                Deref();
                Op(new LocalGet(LAtSlot));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new LocalSet(LAtSlot));
                Op(new Branch(0));                          // -> $count
            }
            CloseNested();                                  // $count
            CloseNested();                                  // $counted

            // Only a spine ending in [] is ours. Everything else -- an
            // unbound tail, a PSTR, an improper end -- goes to the host.
            Op(new LocalGet(LC0));
            Op(new Int64Constant(_env.AtomCell(AtomTable.EmptyListId)));
            Op(new Int64NotEqual());
            Op(new BranchIf(0));                            // -> $slow

            // Empty L1: the answer IS L2.
            Op(new LocalGet(LAtSlot));
            Op(new Int32Constant(0));
            Op(new Int32Equal());
            OpenIf();
            {
                EmitUnifyTwo(() => RegLoad(2), () => RegLoad(1), pc);
                Op(new Branch(2));                          // -> $done
            }
            CloseNested();

            // ---- room for 2N + 1 cells, or step aside ----
            Op(new LocalGet(LH));
            Op(new LocalGet(LAtSlot));
            Op(new Int32Constant(1));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            LoadSlot32(WasmAbi.HeapWatermark);
            Op(new Int32GreaterThanOrEqualSigned());
            OpenIf();
            EmitDeopt(pc, 19);
            CloseNested();

            Op(new LocalGet(LH));
            Op(new LocalSet(LMetaBase));                    // start
            Op(new LocalGet(LH));
            Op(new LocalGet(LAtSlot));
            Op(new Int32Constant(1));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new Int32Constant(1));
            Op(new Int32Add());
            Op(new LocalSet(LH));

            // ---- pass two: write the pairs ----
            RegLoad(0); Op(new LocalSet(LC0)); Deref();
            Op(new Int32Constant(0)); Op(new LocalSet(LMetaGuard));  // i
            OpenBlock();                                    // $built
            OpenLoop();                                     // $build
            {
                Op(new LocalGet(LMetaGuard));
                Op(new LocalGet(LAtSlot));
                Op(new Int32GreaterThanOrEqualSigned());
                Op(new BranchIf(1));                        // -> $built

                // lisIdx = start + 2i
                Op(new LocalGet(LMetaBase));
                Op(new LocalGet(LMetaGuard));
                Op(new Int32Constant(1));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new LocalSet(LT1));

                // heap[lisIdx] = Lis(lisIdx + 1)
                CellStoreDyn(LHeapB, LT1, 0, () =>
                {
                    Op(new Int64Constant((long)Tag.Lis << Cell.TagShift));
                    Op(new LocalGet(LT1));
                    Op(new Int32Constant(1));
                    Op(new Int32Add());
                    Op(new Int64ExtendInt32Unsigned());
                    Op(new Int64Or());
                });

                // heap[lisIdx + 1] = head, where head = heap[cursor.payload]
                Op(new LocalGet(LC0));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new Int32WrapInt64());
                Op(new LocalSet(LT0));
                CellStoreDyn(LHeapB, LT1, 1, () => CellLoadDyn(LHeapB, LT0));

                // cursor = deref(heap[cursor.payload + 1])
                CellLoadDyn(LHeapB, LT0, 1);
                Op(new LocalSet(LC0));
                Deref();

                Op(new LocalGet(LMetaGuard));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new LocalSet(LMetaGuard));
                Op(new Branch(0));                          // -> $build
            }
            CloseNested();                                  // $build
            CloseNested();                                  // $built

            // The last tail is L2 itself.
            Op(new LocalGet(LMetaBase));
            Op(new LocalGet(LAtSlot));
            Op(new Int32Constant(1));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new LocalSet(LT1));
            CellStoreDyn(LHeapB, LT1, 0, () => RegLoad(1));

            // L3 is the cell AT start -- which is already the first pair's
            // LIS -- not Lis(start). Lis() takes a HEAD index, so building
            // one over start wraps the answer in itself: the list came out
            // as [[a,b,c]|a], correct and one level too deep.
            EmitUnifyTwo(() => RegLoad(2),
                         () => CellLoadDyn(LHeapB, LMetaBase), pc);
            Op(new Branch(1));                              // -> $done

            CloseNested();                                  // $slow
            emitBuiltinExit();
            CloseNested();                                  // $done
        }

        /// <summary>The widest meta-call the module takes. The callee's
        /// arguments are copied into X registers, and the bank is staged from
        /// a demand computed at COMPILE time, so a runtime arity past this
        /// has nowhere to land: it steps aside instead. Eight covers every
        /// propagator clpfd and clpr post.</summary>
        private const int MaxMetaCallArity = 8;

        /// <summary>call/1 of a goal known only at RUN time, dispatched inside
        /// the module.
        ///
        /// <para>This is the one thing a module could not do. Every call the
        /// emitter bakes names its callee by a MARKER, and a marker is
        /// interned by the host from a (functor, address) pair -- it cannot be
        /// computed from a functor -- so a meta-call had no target to jump to
        /// and stepped aside, always. The call-marker table closes exactly
        /// that gap: functor id in, fresh-entry marker out, and from there the
        /// ordinary resume probe takes over unchanged.</para>
        ///
        /// <para>Measured, this is where the deopts were. clpfd's propagation
        /// loop is `clpfd_run([P|Ps]) :- call(P), clpfd_run(Ps)`, one step
        /// aside per propagator: 16,378 in a single queens 12 run, 86% of
        /// every deopt in it, and half of all its chains -- because a deopt
        /// closes the chain and the re-entry pays a fresh staging. clpr's
        /// wakeup drain is the same shape.</para>
        ///
        /// <para>Only a COMPOUND goal is taken. An atom goal would need the
        /// functor id of (atom, 0) and the module can read the functor table
        /// but not search it, so those step aside. So does an unmirrored
        /// functor, which reads as arity zero -- impossible for a compound,
        /// and the safe direction.</para></summary>
        /// <param name="tail">A meta-call in TAIL position inherits our
        /// continuation, so CP is left alone and the goal simply takes our
        /// place. The cut barrier is refreshed either way: a tail call still
        /// enters a new procedure, and a neck_cut there must see B as of this
        /// dispatch.</param>
        /// <param name="barrierFromX1">'$call'/2: the barrier is
        /// CARRIED, in X1, rather than being B as of this dispatch. It is the
        /// barrier the enclosing call established, so a cut inside the goal
        /// commits no further than that call -- taking B here instead would let
        /// it prune choice points the caller still owns.</param>
        /// <summary>Stamps WHICH guard sent a meta-call aside into DiagA, so
        /// a decline can be told apart from the other ways to reach the same
        /// deopt. The ABI keeps DiagA for exactly this.</summary>
        private void MetaGuard(int code)
        {
            if (!DebugMetaGuards) return;
            StoreSlot64(WasmAbi.DiagA, () => Op(new Int64Constant(code)));
        }

        private void EmitInlineMetaCall(int pc, int selfFid, bool tail = false,
                                        bool barrierFromX1 = false, int envTrim = 0,
                                        int appended = 0)
        {
            if (MaxMetaCallArity - 1 > _maxRegister) _maxRegister = MaxMetaCallArity - 1;
            EmitFlagsCheck(pc);
            // Decided once, for BOTH arms. The resume cursor at pc + 9 pops
            // the frame this call built, so every path that reaches it must
            // have built one -- the inline cut below jumps straight there,
            // and without its own Allocate it would pop a frame that was
            // never pushed, leaving CP and E holding whatever was on the
            // stack. That is not a failing test, it is a wrong answer.
            bool ownFrame = !tail && !ClauseHasFrame(pc);

            // A goal the module already open-codes, run on the GOAL's own
            // arguments rather than on registers.
            //
            // The meta-call knows one trick -- jump to a predicate -- so a
            // goal that is not one leaves the module even when the form is
            // already sitting in this file. =/2 is the case that proves it:
            // written in a body it is not a call at all (the WAM lowers it to
            // get/unify, and what survives goes through EmitInlineUnify), but
            // arriving through call/1 it is a TERM, compiled by nobody, and
            // the host dispatches it by functor. No marker names it and none
            // ever will.
            //
            // A LAST RESORT, never a precondition. Tried only where the
            // meta-call has just established that no module covers this goal
            // -- the marker is zero, or the $mqual cache has no row -- which
            // is the path that deopted until now. Putting the chain in front
            // of the marker probe, which is the obvious place, taxes the
            // common case (a goal that IS a predicate) with a dozen compares
            // it will never use, to speed up the case that was leaving
            // anyway. The two are exclusive by construction: a form is a
            // builtin, a marker names a predicate, and where a user
            // predicate shadows a builtin name the marker exists and must
            // win.
            //
            // Emitted at both give-up points -- the bare goal's and the one
            // inside $mqual -- because a wrapped goal gives up at the cache
            // probe, and a builtin is never in that cache: the host
            // publishes only the resolutions that end in a jump.
            void EmitInlineGoalForm()
            {
                // The goal's heap index and its functor cell, in locals of
                // this path's OWN: the bodies spend LC0-LC2, LT0-LT2 and LDa,
                // Deref spends LC1 on the way, and a body may spend anything
                // else it likes. Borrowing LAt* here was wrong -- get_attr/3's
                // form overwrites LAtSlot with the hash slot it is probing,
                // and Arg(2), which is loaded only AFTER the probe, then read
                // a heap cell picked by a hash. Measured: get_attr answered
                // with a cell from nowhere, and the comparison after it
                // failed. A form body owes this path nothing.
                Op(new LocalGet(LT2));
                Op(new LocalSet(LGoalBase));
                CellLoadDyn(LHeapB, LT2);
                Op(new LocalSet(LGoalCell));

                // Argument i of the goal, straight off the heap. NOT copied
                // into X0..Xn-1 first, which is the obvious shape and is
                // wrong: X0 holds THE GOAL, and every form here can hand the
                // instruction back to the host -- by declining to $slow, or
                // by stepping aside from inside -- whereupon the host
                // re-reads the goal out of X0 and finds the goal's first
                // argument instead. The jump path may overwrite those
                // registers precisely because it never comes back.
                Action Arg(int i) => () => CellLoadDyn(LHeapB, LGoalBase, i + 1);

                // A form that cannot decide hands the GOAL to the host as an
                // ordinary builtin request -- the goal here IS a builtin, its
                // registry impl is the real one, and a leaf request keeps the
                // chain open where a deopt closes it and leaves the rest of
                // the clause to the interpreter. The arguments go into the
                // registers only NOW: this exit runs the goal and resumes at
                // pc + 9, so the instruction is never re-dispatched and X0
                // is not needed again.
                //
                // Only without an own frame. The wasm resume at pc + 9 pops
                // whatever frame the site built, but a builtin that queues a
                // WAKEUP makes the host close the chain and continue in
                // BYTECODE at pc + 9 -- where no Deallocate exists, and a
                // frame only the wasm emission knows about would be left
                // under E for the caller to read Y slots out of. A site with
                // a real frame (or in tail position) has no such asymmetry.
                Action? DeclineTo(int builtinId, int arity) => ownFrame
                    ? null
                    : () =>
                    {
                        for (int i = 0; i < arity; i++)
                        {
                            int k = i;
                            RegStore(k, Arg(k));
                        }
                        StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                            _env.EncodeBuiltinId(builtinId, tail ? 0 : envTrim))));
                        StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(
                            tail ? -1 : _env.EncodeAddress(pc + 9))));
                        EmitReturn(WasmVerdict.BuiltinRequest);
                    };

                foreach (var (goalFid, builtinId) in _env.MetaCallableBuiltins)
                {
                    Action body;
                    if (_env.IsInlineUnify(builtinId))
                        body = () => EmitUnifyTwo(Arg(0), Arg(1), pc,
                                                  DeclineTo(builtinId, 2));
                    else if (_env.IsInlineCompare(builtinId, out bool negated))
                        body = () => EmitInlineCompare(pc, negated,
                            DeclineTo(builtinId, 2) ?? GoSlow, Arg(0), Arg(1));
                    else if (_env.TryGetInlineTypeTest(builtinId, out var test)
                             && test != WasmTypeTest.None)
                        body = () => EmitInlineTypeTest(test, pc, Arg(0));
                    else if (_env.IsInlineGetAttr(builtinId))
                        body = () => EmitInlineGetAttr(pc,
                            DeclineTo(builtinId, 3) ?? GoSlow, Arg(0), Arg(1), Arg(2));
                    else
                        continue;

                    Op(new LocalGet(LGoalCell));
                    Op(new Int64Constant(_env.FunctorCell(goalFid)));
                    Op(new Int64Equal());
                    OpenIf();
                    {
                        body();
                        // Only now: a form that fails, declines or steps
                        // aside leaves BEFORE this point, and the host has to
                        // find the frame exactly as it was.
                        if (ownFrame)
                        {
                            EmitAllocateFrame(0, pc);
                            _metaFrameResume.Add(pc + 9);
                        }
                        GoTo(pc + 9);
                    }
                    CloseNested();
                }
            }

            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow
            // $slow from any depth: a form that cannot decide falls back to
            // exactly what the goal did before there were forms.
            int slowDepth = _extraDepth;
            void GoSlow() => Op(new Branch((uint)(_extraDepth - slowDepth)));

            LoadSlot32(WasmAbi.CallMarkerBase);
            Op(new LocalSet(LT0));
            Op(new LocalGet(LT0));
            Op(new Int32Constant(0));
            Op(new Int32Equal());
            MetaGuard(1);
            Op(new BranchIf(0));                            // no table -> $slow

            // The goal must be a compound: an atom carries no functor id.
            RegLoad(0); Op(new LocalSet(LC0)); Deref();

            // A bare ATOM goal is a zero-arity predicate. The module has its
            // atom id and cannot turn (atom, 0) into a functor -- that is a
            // SEARCH of the functor table and it can only index -- so the
            // host publishes the marker by atom instead. Measured, this is a
            // third of clpr's remaining deopts: the prelude's disjunction
            // helpers reach their branches through '$call'/2, and those
            // branches are atoms.
            TagOfC0();
            Op(new Int32Constant((int)Tag.Atom));
            Op(new Int32Equal());
            OpenIf();
            {
                // An ATOM goal is out of reach for call/N. The table
                // below is keyed by atom and names the name/0 predicate;
                // an appending call site wants name/appended, and the
                // module cannot form that functor id -- interning is a
                // search of the functor table and it can only index. So
                // the atom form of call/N still steps aside; only the
                // partially applied (compound) goal is served here.
                if (appended != 0)
                {
                    MetaGuard(13);
                    GoSlow();
                }
                // The goal is `!`. Measured, this is what '$call'/2 carries
                // in clpr: the prelude expands a disjunction into helpers
                // that run each branch through it, and a branch is often the
                // cut itself. It has no marker and never will -- a cut is not
                // a predicate to jump to -- which is why the atom table alone
                // left the deopts where they were.
                //
                // The barrier is the one X1 carries, which is the whole point
                // of '$call'/2: the cut commits no further than the call that
                // established it.
                if (barrierFromX1)
                {
                    Op(new LocalGet(LC0));
                    Op(new Int64Constant(_env.AtomCell(CutAtomId)));
                    Op(new Int64Equal());
                    OpenIf();
                    {
                        // A live setup_call_cleanup handler makes a cut run
                        // cleanups, and running one is meta-calling a goal
                        // from inside the cut. That is host work.
                        LoadSlot32(WasmAbi.CleanupsPending);
                        Op(new Int32Constant(0));
                        Op(new Int32NotEqual());
                        MetaGuard(16);
                        Op(new BranchIf(2));                // -> $slow
                        // X1 is a cell, and a cell can be a REF chain: the
                        // host reads this barrier through a deref. Taking the
                        // payload raw yields a HEAP INDEX, which is a small
                        // positive number and therefore a plausible-looking
                        // barrier -- it cuts, just to the wrong place, and
                        // takes the caller's choice points with it.
                        //
                        // Deref works on LC0 and clobbers it. Safe here only
                        // because both ways out of this arm are exits: the
                        // $slow branch re-reads the registers in the host.
                        RegLoad(1);
                        Op(new LocalSet(LC0));
                        Deref();
                        EmitCut(() =>
                        {
                            Op(new LocalGet(LC0));
                            Op(new Int32WrapInt64());
                        });
                        // Symmetry with the calling path: pc + 9 pops a frame.
                        if (ownFrame)
                        {
                            EmitAllocateFrame(0, pc);
                            _metaFrameResume.Add(pc + 9);
                        }
                        // A cut SUCCEEDS and execution carries on at the next
                        // instruction, exactly as the host's does.
                        GoTo(pc + 9);
                    }
                    CloseNested();
                }

                LoadSlot32(WasmAbi.AtomMarkerBase);
                Op(new LocalSet(LT0));
                Op(new LocalGet(LT0));
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                MetaGuard(13);
                Op(new BranchIf(1));                        // no table -> $slow

                Op(new LocalGet(LC0));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new Int32WrapInt64());
                Op(new LocalSet(LT1));                      // atom id
                Op(new LocalGet(LT1));
                LoadSlot32(WasmAbi.AtomMarkerLength);
                Op(new Int32GreaterThanOrEqualUnsigned());
                MetaGuard(14);
                Op(new BranchIf(1));                        // past it -> $slow

                Op(new LocalGet(LT0));
                Op(new LocalGet(LT1));
                Op(new Int32Constant(2));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new Int32Load());
                Op(new LocalSet(LAtVal));                   // marker
                Op(new LocalGet(LAtVal));
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                MetaGuard(15);
                if (DebugMetaGuards)
                    StoreSlot64(WasmAbi.DiagB, () =>
                    {
                        Op(new LocalGet(LT1));
                        Op(new Int64ExtendInt32Unsigned());
                    });
                Op(new BranchIf(1));                        // uncovered -> $slow

                // No arguments to move, and no two arities to reconcile.
                Op(new Int32Constant(0));
                Op(new LocalSet(LT1));                      // arity
                Op(new Int32Constant(-1));
                Op(new LocalSet(LMetaArity));
                EmitReadBarrier();
                EmitMetaTail();
            }
            CloseNested();

            TagOfC0();
            Op(new Int32Constant((int)Tag.Str));
            Op(new Int32NotEqual());
            MetaGuard(2);
            Op(new BranchIf(0));                            // -> $slow
            Op(new LocalGet(LC0));
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT2));                          // the structure's index
            CellLoadDyn(LHeapB, LT2);
            Op(new LocalSet(LC1));                          // its functor cell

            // '$mqual'(Module, Goal): the goal carries the module of the
            // clause that meta-called it, because a bare functor has to
            // resolve against that module's locals first. The module cannot
            // do that resolution -- it is a walk through a module's locals
            // and imports -- so it reads what the HOST resolved, out of the
            // inline cache the host fills on the path it was taking anyway.
            //
            // Compared as a CELL, never as a baked id: FunctorCell relocates
            // by (name, arity), and a functor id is only valid in the process
            // that interned it.
            Op(new LocalGet(LC1));
            Op(new Int64Constant(_env.FunctorCell(_env.MqualFunctorId)));
            Op(new Int64Equal());
            OpenIf();
            {
                // Module must be a bound atom.
                CellLoadDyn(LHeapB, LT2, 1);
                Op(new LocalSet(LC0));
                Deref();
                TagOfC0();
                Op(new Int32Constant((int)Tag.Atom));
                Op(new Int32NotEqual());
                MetaGuard(6);
                Op(new BranchIf(1));                        // -> $slow
                Op(new LocalGet(LC0));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new Int32WrapInt64());
                Op(new LocalSet(LT0));                      // module atom

                // The goal inside is a compound, or an ATOM -- which is a
                // predicate of arity 0 before this call site appends to it,
                // and the shape clp(Z)'s maplist/3 actually carries: its
                // goal arrives in a variable holding a bare predicate name.
                // Both key the cache; they differ in WHAT they key by, and
                // in that an atom has no arguments to copy.
                CellLoadDyn(LHeapB, LT2, 2);
                Op(new LocalSet(LC0));
                Deref();
                TagOfC0();
                Op(new Int32Constant((int)Tag.Str));
                Op(new Int32Equal());
                OpenIf();
                {
                    Op(new Int32Constant(0));
                    Op(new LocalSet(LMetaAtom));
                    Op(new LocalGet(LC0));
                    Op(new Int32WrapInt64());
                    Op(new LocalSet(LT2));                  // the GOAL's index
                    CellLoadDyn(LHeapB, LT2);
                    Op(new Int64Constant(Cell.PayloadMask));
                    Op(new Int64And());
                    Op(new Int32WrapInt64());
                    Op(new LocalSet(LT1));                  // the goal's functor

                    // The goal's own arity, kept for the cross-check below:
                    // the args are copied out of the GOAL, but the callee's
                    // width comes from the RESOLVED functor, and the two
                    // have to agree once this site's appended count is in.
                    LoadSlot32(WasmAbi.FunctorTableBase);
                    Op(new LocalGet(LT1));
                    Op(new Int32Constant(3));
                    Op(new Int32ShiftLeft());
                    Op(new Int32Add());
                    Op(new Int64Load());
                    Op(new Int32WrapInt64());
                    Op(new LocalSet(LMetaArity));           // goal arity
                }
                OpenElse();
                {
                    // An atom keys by its ATOM id. Interning (name, 0) is a
                    // search of the functor table and a module can only
                    // index, so the functor is not available here at all --
                    // which is why the key carries a flag: atom ids and
                    // functor ids share their range.
                    TagOfC0();
                    Op(new Int32Constant((int)Tag.Atom));
                    Op(new Int32NotEqual());
                    OpenIf();
                    {
                        MetaGuard(7);
                        GoSlow();
                    }
                    CloseNested();
                    Op(new Int32Constant(1));
                    Op(new LocalSet(LMetaAtom));
                    Op(new Int32Constant(0));
                    Op(new LocalSet(LMetaArity));           // no arguments
                    Op(new LocalGet(LC0));
                    Op(new Int64Constant(Cell.PayloadMask));
                    Op(new Int64And());
                    Op(new Int32WrapInt64());
                    Op(new LocalSet(LT1));                  // the atom id
                }
                CloseNested();

                LoadSlot32(WasmAbi.MetaCacheBase);
                Op(new LocalSet(LMetaBase));
                Op(new LocalGet(LMetaBase));
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                MetaGuard(8);
                Op(new BranchIf(1));                        // no cache -> $slow

                // key = ((module + 1) << 36) | (atom << 35)
                //     | (appended << 32) | goalKey
                // -- WasmResumeTable.MetaKey, which this must match word for
                // word. call/N resolves to a WIDER functor than the goal,
                // and the module cannot derive that id, so the key is what
                // it can form: the goal, whether the goal is an atom, and
                // how many arguments this site appends.
                Op(new LocalGet(LT0));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new Int64ExtendInt32Signed());
                Op(new Int64Constant(36));
                Op(new Int64ShiftLeft());
                Op(new LocalGet(LMetaAtom));
                Op(new Int64ExtendInt32Unsigned());
                Op(new Int64Constant(35));
                Op(new Int64ShiftLeft());
                Op(new Int64Or());
                Op(new Int64Constant((long)(appended & 7) << 32));
                Op(new Int64Or());
                Op(new LocalGet(LT1));
                Op(new Int64ExtendInt32Unsigned());
                Op(new Int64Or());
                Op(new LocalSet(LAtKey));

                // slot = hash(module, goalFunctor, appended) & mask
                Op(new LocalGet(LT0));
                Op(new Int32Constant(unchecked((int)2654435761u)));
                Op(new Int32Multiply());
                Op(new LocalGet(LT1));
                Op(new Int32Constant(unchecked((int)2246822519u)));
                Op(new Int32Multiply());
                Op(new Int32Add());
                if (appended != 0)
                {
                    // Folded as an immediate: appended is known here.
                    Op(new Int32Constant(
                        unchecked((int)((uint)appended * 2166136261u))));
                    Op(new Int32Add());
                }
                // The atom flag is only known at run time, so this one is
                // a multiply rather than an immediate.
                Op(new LocalGet(LMetaAtom));
                Op(new Int32Constant(unchecked((int)2654435789u)));
                Op(new Int32Multiply());
                Op(new Int32Add());
                Op(new LocalSet(LAtSlot));
                Op(new LocalGet(LAtSlot));
                Op(new LocalGet(LAtSlot));
                Op(new Int32Constant(15));
                Op(new Int32ShiftRightUnsigned());
                Op(new Int32ExclusiveOr());
                LoadSlot32(WasmAbi.MetaCacheMask);
                Op(new LocalSet(LT0));                      // mask, module spent
                Op(new LocalGet(LT0));
                Op(new Int32And());
                Op(new LocalSet(LAtSlot));
                Op(new LocalGet(LT0));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new LocalSet(LMetaGuard));               // one pass, no more

                OpenBlock();                                // $resolved
                OpenLoop();                                 // $probe
                {
                    Op(new LocalGet(LMetaBase));
                    Op(new LocalGet(LAtSlot));
                    Op(new Int32Constant(4));
                    Op(new Int32ShiftLeft());
                    Op(new Int32Add());
                    Op(new LocalSet(LT1));                  // the row
                    Op(new LocalGet(LT1));
                    Op(new Int64Load());
                    Op(new LocalSet(LC1));

                    // An empty slot: the host has not resolved this pair yet.
                    // Step aside, and it will -- and fill the cache doing it.
                    Op(new LocalGet(LC1));
                    Op(new Int64Constant(0));
                    Op(new Int64Equal());
                    OpenIf();
                    {
                        EmitInlineGoalForm();
                        MetaGuard(9);
                        GoSlow();
                    }
                    CloseNested();

                    Op(new LocalGet(LC1));
                    Op(new LocalGet(LAtKey));
                    Op(new Int64Equal());
                    OpenIf();
                    {
                        Op(new LocalGet(LT1));
                        Op(new Int64Load { Offset = 8 });
                        Op(new Int32WrapInt64());
                        Op(new LocalSet(LT1));              // the RESOLVED functor
                        Op(new Branch(2));                  // -> $resolved
                    }
                    CloseNested();

                    Op(new LocalGet(LAtSlot));
                    Op(new Int32Constant(1));
                    Op(new Int32Add());
                    LoadSlot32(WasmAbi.MetaCacheMask);
                    Op(new Int32And());
                    Op(new LocalSet(LAtSlot));
                    Op(new LocalGet(LMetaGuard));
                    Op(new Int32Constant(1));
                    Op(new Int32Subtract());
                    Op(new LocalSet(LMetaGuard));
                    Op(new LocalGet(LMetaGuard));
                    Op(new Int32Constant(0));
                    Op(new Int32Equal());
                    MetaGuard(10);
                    Op(new BranchIf(3));                    // exhausted -> $slow
                    Op(new Branch(0));                      // -> $probe
                }
                CloseNested();                              // $probe
                CloseNested();                              // $resolved
            }
            OpenElse();
            {
                // Not wrapped: the callee IS the goal, so there are not two
                // arities to reconcile.
                Op(new Int32Constant(0));
                Op(new LocalSet(LMetaAtom));
                Op(new Int32Constant(-1));
                Op(new LocalSet(LMetaArity));
                Op(new LocalGet(LC1));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new Int32WrapInt64());
                Op(new LocalSet(LT1));                      // fid
            }
            CloseNested();

            if (DebugMetaGuards)
                StoreSlot64(WasmAbi.DiagB, () =>
                {
                    Op(new LocalGet(LT1));
                    Op(new Int64ExtendInt32Unsigned());
                });

            // marker = callMarkers[fid]; zero means no module covers it, and
            // an id past the table reads as zero for the same reason -- both
            // mean "not a predicate this world compiled", which is the ONE
            // question that decides between jumping and everything else.
            // The base is re-read rather than held: LT0 was spent inside the
            // $mqual branch, and a mailbox load is cheaper than a local more.
            Op(new LocalGet(LT1));
            LoadSlot32(WasmAbi.CallMarkerLength);
            Op(new Int32GreaterThanOrEqualUnsigned());
            OpenIf();
            {
                Op(new Int32Constant(0));
                Op(new LocalSet(LAtVal));
            }
            OpenElse();
            {
                LoadSlot32(WasmAbi.CallMarkerBase);
                Op(new LocalGet(LT1));
                Op(new Int32Constant(2));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new Int32Load());
                Op(new LocalSet(LAtVal));                   // marker, to the probe
            }
            CloseNested();
            Op(new LocalGet(LAtVal));
            Op(new Int32Constant(0));
            Op(new Int32Equal());
            OpenIf();
            {
                EmitInlineGoalForm();
                MetaGuard(4);
                GoSlow();
            }
            CloseNested();

            // arity, from the functor table's mirror. An id the host has not
            // mirrored reads as zero, which no compound can be.
            LoadSlot32(WasmAbi.FunctorTableBase);
            Op(new LocalGet(LT1));
            Op(new Int32Constant(3));
            Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new Int64Load());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));                          // arity, fid spent
            Op(new LocalGet(LT1));
            Op(new Int32Constant(1));
            Op(new Int32Subtract());
            Op(new Int32Constant(MaxMetaCallArity - 1));
            Op(new Int32GreaterThanUnsigned());
            MetaGuard(5);
            Op(new BranchIf(0));                            // 0, or too wide -> $slow

            // The resolved functor must have the goal's arity plus whatever
            // this call site appends -- call(G, X) resolves to a predicate
            // one wider than G, and call/1 to one exactly as wide. Mangling
            // preserves it, so this only ever fires on a cache that is
            // telling the truth about the wrong pair -- but without it the
            // copy below would read past the goal's arguments and call
            // another predicate with whatever followed them on the heap.
            // Degrading safely should not depend on the wrong answer
            // happening to name a predicate no module covers.
            if (appended != 0)
            {
                Op(new LocalGet(LMetaArity));
                Op(new Int32Constant(0));
                Op(new Int32LessThanSigned());
                MetaGuard(11);
                Op(new BranchIf(0));                        // -> $slow
            }
            Op(new LocalGet(LMetaArity));
            Op(new Int32Constant(0));
            Op(new Int32GreaterThanOrEqualSigned());
            OpenIf();
            {
                Op(new LocalGet(LMetaArity));
                if (appended != 0)
                {
                    Op(new Int32Constant(appended));
                    Op(new Int32Add());
                }
                Op(new LocalGet(LT1));
                Op(new Int32NotEqual());
                MetaGuard(11);
                Op(new BranchIf(1));                        // -> $slow
            }
            CloseNested();

            // LAST chance: the copy below overwrites X1.
            EmitReadBarrier();

            // call/N: the arguments to append are sitting in X1..Xappended,
            // and the copy below writes X0 upwards -- so it would eat them
            // before they are placed. Park them above MaxMetaCallArity,
            // which no callee reaches, and put them back once the goal's
            // own arguments are down. Unrolled: appended is known here.
            for (int i = 1; i <= appended; i++)
            {
                int park = MaxMetaCallArity + i - 1;
                if (park > _maxRegister) _maxRegister = park;
                RegStore(park, () => RegLoad(i));
            }

            // The goal's arguments become X0..Xn-1. An ATOM goal has none
            // and LMetaArity is 0, so this loop runs zero times -- which is
            // what keeps LT2 (not a goal index in that case) unread.
            Op(new Int32Constant(0));
            Op(new LocalSet(LAtSlot));
            OpenBlock();                                    // $copied
            OpenLoop();                                     // $copy
            {
                Op(new LocalGet(LAtSlot));
                Op(new LocalGet(appended != 0 ? LMetaArity : LT1));
                Op(new Int32GreaterThanOrEqualUnsigned());
                Op(new BranchIf(1));                        // -> $copied

                Op(new LocalGet(LRegsB));
                Op(new LocalGet(LAtSlot));
                Op(new Int32Constant(3));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new LocalGet(LHeapB));
                Op(new LocalGet(LT2));
                Op(new LocalGet(LAtSlot));
                Op(new Int32Add());
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new Int32Constant(3));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new Int64Load());
                Op(new Int64Store());

                Op(new LocalGet(LAtSlot));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new LocalSet(LAtSlot));
                Op(new Branch(0));                          // -> $copy
            }
            CloseNested();                                  // $copy
            CloseNested();                                  // $copied

            // ...and the appended ones follow them, at X[goalArity + i].
            // The destination is only known at run time, so the address is
            // computed; the source is a parking slot, which is a constant.
            for (int i = 0; i < appended; i++)
            {
                Op(new LocalGet(LRegsB));
                Op(new LocalGet(LMetaArity));
                if (i != 0)
                {
                    Op(new Int32Constant(i));
                    Op(new Int32Add());
                }
                Op(new Int32Constant(3));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                RegLoad(MaxMetaCallArity + i);
                Op(new Int64Store());
            }

            // From here it is an ordinary call: the callee enters a new
            // procedure, so its cut barrier is refreshed, and CP is the marker
            // that brings it back to the instruction after this one.
            // A non-tail meta-call WRITES CP, and that is only recoverable
            // when a frame holds the clause's own continuation. A clause
            // whose only call was a builtin has no frame and needs none --
            // so build one here, exactly the shape Allocate/Deallocate
            // already agree on, rather than decline the site. Zero Y slots:
            // its whole job is the saved CP and E.
            //
            // The tail form writes no CP at all -- it inherits ours -- so it
            // needs nothing.
            // The shared tail. A local function and not a copy: both
            // arms -- an atom goal and a compound one -- reach the call
            // the same way, and the frame, the barrier, the CP and the
            // probe are exactly what must not drift between them.
            //
            // Called from inside the atom arm and again at the end, so
            // each arm emits its own copy INTO ITS OWN BRANCH. That is
            // what keeps the compound path at the block depth it was
            // written for: wrapping it to share one copy would shift
            // every branch target in it, and a wrong one there is a
            // hang, not a failed test.
            // X1 carries the barrier ONLY until the goal's arguments are
            // copied over the registers. Read it, check it and park it BEFORE
            // that -- and the check has to happen here too, because a barrier
            // this path cannot use sends the instruction back to the host,
            // which re-reads '$call'(Goal, Barrier) out of X0 and X1. Doing
            // either after the copy hands the host the GOAL'S arguments
            // instead: measured, X0 came back a plain Ref and the goal itself
            // turned up in X1.
            void EmitReadBarrier()
            {
                if (!barrierFromX1) return;
                RegLoad(1); Op(new LocalSet(LC0)); Deref();
                TagOfC0();
                Op(new Int32Constant((int)Tag.Int));
                Op(new Int32NotEqual());
                MetaGuard(12);
                GoSlow();
                Op(new LocalGet(LC0));
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new LocalSet(LMetaBarrier));
            }

            void EmitMetaTail()
            {
                    if (ownFrame)
                {
                    EmitAllocateFrame(0, pc);
                    _metaFrameResume.Add(pc + 9);
                }
                if (barrierFromX1)
                {
                    // Already read, checked and parked by EmitReadBarrier,
                    // back when X1 still held it.
                    StoreSlot64(WasmAbi.CutBarrier,
                                () => Op(new LocalGet(LMetaBarrier)));
                }
                else
                {
                    StoreSlotFromI32Local(WasmAbi.CutBarrier, LB);
                }
                if (!tail)
                {
                    Op(new Int32Constant(_env.EncodeReturnMarker(selfFid, pc + 9)));
                    Op(new LocalSet(LCP));
                }

                // The watermark guard first, for the same reason every other call
                // has one: at or past it the host owes a collection, and staying
                // inside wasm would skip the boundary where it happens.
                Op(new LocalGet(LH));
                LoadSlot32(WasmAbi.HeapWatermark);
                Op(new Int32LessThanSigned());
                OpenIf();
                EmitResumeProbe(LAtVal);
                CloseNested();
                StoreSlot64(WasmAbi.Pc, () =>
                {
                    Op(new LocalGet(LAtVal));
                    Op(new Int64ExtendInt32Unsigned());
                });
                EmitReturn(WasmVerdict.SuccessTailCall);
            }

            EmitMetaTail();

            CloseNested();                                  // $slow
            EmitDeopt(pc, DeoptStamped);
            CloseNested();                                  // $done
        }

        /// <summary>=/2 open-coded: X0 against X1 through the same two-cell
        /// unify every get_value uses (immediates inline, compounds through
        /// the general unifier, attvars deopt). No host round-trip.</summary>
        private void EmitInlineUnify(int pc, Action? emitEscape = null)
        {
            EmitFlagsCheck(pc);
            EmitUnifyTwo(() => RegLoad(0), () => RegLoad(1), pc, emitEscape);
        }

        /// <summary>Tags for which term identity IS cell identity, so a pair
        /// can be decided without descending: an unbound variable, an
        /// attributed variable, an atom, a small integer.
        ///
        /// <para>ATTVAR belongs here and is the whole reason this got widened.
        /// The engine normalises an attributed variable to a REF at its home
        /// before comparing, so == judges it by identity like any variable --
        /// and clpfd's variables are all attributed, which is why queens 12
        /// left the module 16,793 times for ==/2 and failed 16,763 of
        /// them.</para>
        ///
        /// <para>Not here, each for its own reason: a float, a bignum and a
        /// rational are boxed, so equal values sit in different cells; a
        /// compound has to be walked; a PSTR is excluded for a subtler reason
        /// still.</para></summary>
        private const int SimpleTagMask =
            (1 << (int)Tag.Ref) | (1 << (int)Tag.AttVar)
            | (1 << (int)Tag.Atom) | (1 << (int)Tag.Int);

        /// <summary>A PSTR can normalise to a cell of ANOTHER tag: a
        /// zero-length one IS its own tail, so it compares equal to the atom
        /// [] despite the tags. Deciding such a pair by tags would answer
        /// false, so a PSTR on either side goes to the host whatever the
        /// other side is.
        ///
        /// <para>UNTESTED, and deliberately kept: no Prolog-level query found
        /// reaches == with a zero-length PSTR still in a register, because
        /// unification materialises the tail first, and removing this guard
        /// does not turn any test red. It stays because the cell state is
        /// real -- NormalizeEmptyPstr exists precisely so that every
        /// comparison, type test and ordering collapses it, rather than the
        /// producers never making one -- and a module reads a register
        /// directly, with no consumer in between to normalise it.</para>
        /// </summary>
        private const int PstrTagMask = 1 << (int)Tag.Pstr;

        /// <summary>Term identity (<c>==/2</c> / <c>\==/2</c>), decided inside
        /// the module whenever CELLS alone can decide it.
        ///
        /// <para>Two rules, and between them they cover nearly everything the
        /// solvers ask. Identical cells are identical terms, always. And when
        /// the cells differ, if EITHER side has a tag whose identity is cell
        /// identity (<see cref="SimpleTagMask"/>) the terms differ -- such a
        /// cell is never list-like, so the engine reaches its tag test, and
        /// there either the tags differ or both sides are compared as
        /// cells.</para>
        ///
        /// <para>Over the old form (Atom or Int on BOTH sides) that adds
        /// variables, which is where the exits actually were, and every mixed
        /// pair: a variable against a compound now decides instead of leaving.
        /// Floats, bignums, rationals, two compounds, and anything touching a
        /// PSTR still go to <paramref name="emitBuiltinExit"/>.</para></summary>
        /// <param name="load0">Where argument 0 comes from. Null means the
        /// register a static call site would have put it in. A META-CALLED
        /// goal passes heap loads instead, and MUST: this form can decline or
        /// step aside, and both hand the instruction back to the host, which
        /// re-reads the goal out of X0. Writing the arguments into the
        /// registers first would have destroyed it.</param>
        /// <param name="load1">Where argument 1 comes from. Null means the
        /// register a static call site would have put it in. A META-CALLED
        /// goal passes heap loads instead, and MUST: this form can decline or
        /// step aside, and both hand the instruction back to the host, which
        /// re-reads the goal out of X0. Writing the arguments into the
        /// registers first would have destroyed it.</param>
        private void EmitInlineCompare(int pc, bool negated, Action emitBuiltinExit,
                                      Action? load0 = null, Action? load1 = null)
        {
            EmitFlagsCheck(pc);
            (load0 ?? (() => RegLoad(0)))(); Op(new LocalSet(LC0)); Deref();
            Op(new LocalGet(LC0)); Op(new LocalSet(LC2));
            (load1 ?? (() => RegLoad(1)))(); Op(new LocalSet(LC0)); Deref();

            // (mask >>> tag) & 1: one shift and one and, against four compares,
            // and the tags are not adjacent so a range test is out.
            void TagIn(uint cellLocal, int mask)
            {
                Op(new Int32Constant(mask));
                Op(new LocalGet(cellLocal));
                Op(new Int64Constant(60));
                Op(new Int64ShiftRightUnsigned());
                Op(new Int32WrapInt64());
                Op(new Int32ShiftRightUnsigned());
                Op(new Int32Constant(1));
                Op(new Int32And());
            }

            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow

            // The same cell is the same term, whatever its tag.
            Op(new LocalGet(LC2));
            Op(new LocalGet(LC0));
            Op(new Int64Equal());
            OpenIf();
            if (negated) GoFail();                          // \==: identical -> fail
            else Op(new Branch(2));                         // ==: identical -> done
            CloseNested();

            // Different cells: decidable when one side is simple and NEITHER
            // side is a PSTR.
            TagIn(LC2, SimpleTagMask);
            TagIn(LC0, SimpleTagMask);
            Op(new Int32Or());
            TagIn(LC2, PstrTagMask);
            TagIn(LC0, PstrTagMask);
            Op(new Int32Or());
            Op(new Int32Constant(0));
            Op(new Int32Equal());                           // no PSTR involved
            Op(new Int32And());
            Op(new Int32Constant(0));
            Op(new Int32Equal());
            Op(new BranchIf(0));                            // undecidable -> $slow

            if (negated) Op(new Branch(1));                 // \==: different -> done
            else GoFail();                                  // ==: different -> fail

            CloseNested();                                  // $slow
            emitBuiltinExit();
            CloseNested();                                  // $done
        }

        /// <summary>get_attr/3 answered from the attribute image in linear
        /// memory (WasmAbi.AttrTableBase), instead of stepping out to the
        /// host.
        ///
        /// <para>It is the largest single source of builtin exits measured:
        /// 12,600 in a clpr run against 6,000 for the next one. Only 1,200 of
        /// those FAIL, which is why the image had to be built -- open-coding
        /// just the failing path would have erased under a tenth of them.</para>
        ///
        /// <para>The probe is the host's, instruction for instruction: hash,
        /// then walk, stopping at an empty slot and stepping over tombstones.
        /// It is BOUNDED by the table size. The image's load factor
        /// guarantees an empty slot exists, so the bound is unreachable by
        /// construction -- it is there because the alternative to a wrong
        /// image is a module that never returns, and a wrong answer is
        /// recoverable while a hang is not.</para>
        ///
        /// <para>Everything narrow falls back to <paramref
        /// name="emitBuiltinExit"/>: no image staged, a non-attvar first
        /// argument, an unbound or non-atom module. The builtin's errors and
        /// its allocation for a non-variable stay the engine's, which is what
        /// keeps this a speed change and not a semantic one. A MISS, though,
        /// is answered here: no attribute means fail, and that needs nothing
        /// the module does not have.</para></summary>
        /// <param name="load0">Where argument 0 comes from. Null means the
        /// register a static call site would have put it in. A META-CALLED
        /// goal passes heap loads instead, and MUST: this form can decline or
        /// step aside, and both hand the instruction back to the host, which
        /// re-reads the goal out of X0. Writing the arguments into the
        /// registers first would have destroyed it.</param>
        /// <param name="load1">Where argument 1 comes from. Null means the
        /// register a static call site would have put it in. A META-CALLED
        /// goal passes heap loads instead, and MUST: this form can decline or
        /// step aside, and both hand the instruction back to the host, which
        /// re-reads the goal out of X0. Writing the arguments into the
        /// registers first would have destroyed it.</param>
        /// <param name="load2">Where argument 2 comes from. Null means the
        /// register a static call site would have put it in. A META-CALLED
        /// goal passes heap loads instead, and MUST: this form can decline or
        /// step aside, and both hand the instruction back to the host, which
        /// re-reads the goal out of X0. Writing the arguments into the
        /// registers first would have destroyed it.</param>
        private void EmitInlineGetAttr(int pc, Action emitBuiltinExit,
                                      Action? load0 = null, Action? load1 = null,
                                      Action? load2 = null)
        {
            EmitFlagsCheck(pc);
            OpenBlock();                                    // $done
            OpenBlock();                                    // $slow

            // No image: the host still knows how to do this.
            LoadSlot32(WasmAbi.AttrTableBase);
            Op(new LocalSet(LT0));
            Op(new LocalGet(LT0));
            Op(new Int32Constant(0));
            Op(new Int32Equal());
            Op(new BranchIf(0));                            // -> $slow

            // A1 FIRST, and the order is the contract, not a preference.
            // The builtin resolves the module before it ever looks the
            // attribute up, so get_attr(Var, NotAnAtom, V) RAISES whatever
            // A0 is. Deciding A0 first would let the fail arm below answer
            // "no" to a call that owes an error.
            //
            // A bound atom, then: an unbound or non-atom module is an ERROR,
            // and errors are the host's.
            (load1 ?? (() => RegLoad(1)))(); Op(new LocalSet(LC0)); Deref();
            TagOfC0();
            Op(new Int32Constant((int)Tag.Atom));
            Op(new Int32NotEqual());
            Op(new BranchIf(0));                            // -> $slow
            Op(new LocalGet(LC0));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));                          // module atom id

            // A0 must be an attributed variable. Its payload IS its home.
            (load0 ?? (() => RegLoad(0)))(); Op(new LocalSet(LC0)); Deref();
            TagOfC0();
            Op(new Int32Constant((int)Tag.AttVar));
            Op(new Int32NotEqual());
            OpenIf();
            {
                // A PLAIN unbound variable carries no attributes at all --
                // carrying one is what makes a variable an ATTVAR -- so
                // get_attr on it fails, in every module, and answering that
                // needs nothing the module does not already have. Measured
                // in the browser: clpr left the module 400 times for this
                // and failed all 400, one host round trip each to be told
                // no.
                //
                // Anything else is still the host's: a BOUND first argument
                // is the builtin's own business, error or fail.
                TagOfC0();
                Op(new Int32Constant((int)Tag.Ref));
                Op(new Int32Equal());
                OpenIf();
                GoFail();
                CloseNested();
                Op(new Branch(1));                          // -> $slow
            }
            CloseNested();
            Op(new LocalGet(LC0));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int32WrapInt64());
            Op(new LocalSet(LAtVal));                       // home, for now

            // key = ((home + 1) << 32) | (uint)module
            Op(new LocalGet(LAtVal));
            Op(new Int32Constant(1));
            Op(new Int32Add());
            Op(new Int64ExtendInt32Signed());
            Op(new Int64Constant(32));
            Op(new Int64ShiftLeft());
            Op(new LocalGet(LT1));
            Op(new Int64ExtendInt32Unsigned());
            Op(new Int64Or());
            Op(new LocalSet(LAtKey));

            // slot = ((home * 2654435761 + module * 2246822519) ^ (h >>> 15)) & mask
            Op(new LocalGet(LAtVal));
            Op(new Int32Constant(unchecked((int)2654435761u)));
            Op(new Int32Multiply());
            Op(new LocalGet(LT1));
            Op(new Int32Constant(unchecked((int)2246822519u)));
            Op(new Int32Multiply());
            Op(new Int32Add());
            Op(new LocalSet(LAtSlot));
            Op(new LocalGet(LAtSlot));
            Op(new LocalGet(LAtSlot));
            Op(new Int32Constant(15));
            Op(new Int32ShiftRightUnsigned());
            Op(new Int32ExclusiveOr());
            LoadSlot32(WasmAbi.AttrTableMask);
            Op(new LocalSet(LT1));                          // mask, module id spent
            Op(new LocalGet(LT1));
            Op(new Int32And());
            Op(new LocalSet(LAtSlot));

            // The bound: one pass over the table and no more.
            Op(new LocalGet(LT1));
            Op(new Int32Constant(1));
            Op(new Int32Add());
            Op(new LocalSet(LT2));

            OpenBlock();                                    // $found
            OpenLoop();                                     // $probe
            {
                // k = image[slot].key
                Op(new LocalGet(LT0));
                Op(new LocalGet(LAtSlot));
                Op(new Int32Constant(4));
                Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new LocalSet(LT1));                      // the row's address
                Op(new LocalGet(LT1));
                Op(new Int64Load());
                Op(new LocalSet(LC1));

                // An empty slot ends the probe: no such attribute, so FAIL.
                Op(new LocalGet(LC1));
                Op(new Int64Constant(0));
                Op(new Int64Equal());
                OpenIf();
                GoFail();
                CloseNested();

                // A hit: take the value and leave.
                Op(new LocalGet(LC1));
                Op(new LocalGet(LAtKey));
                Op(new Int64Equal());
                OpenIf();
                {
                    Op(new LocalGet(LT1));
                    Op(new Int64Load { Offset = 8 });
                    Op(new Int32WrapInt64());
                    Op(new LocalSet(LAtVal));
                    Op(new Branch(2));                      // -> $found
                }
                CloseNested();

                // A tombstone or another key: step over it.
                LoadSlot32(WasmAbi.AttrTableMask);
                Op(new LocalSet(LT1));
                Op(new LocalGet(LAtSlot));
                Op(new Int32Constant(1));
                Op(new Int32Add());
                Op(new LocalGet(LT1));
                Op(new Int32And());
                Op(new LocalSet(LAtSlot));

                Op(new LocalGet(LT2));
                Op(new Int32Constant(1));
                Op(new Int32Subtract());
                Op(new LocalSet(LT2));
                Op(new LocalGet(LT2));
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                Op(new BranchIf(2));                        // exhausted -> $slow
                Op(new Branch(0));                          // -> $probe
            }
            CloseNested();                                  // $probe
            CloseNested();                                  // $found

            // Value in hand: unify A2 with the attribute term, exactly as
            // UnifyRegisterWithHeapAt does. The general shapes inside step
            // aside on their own, so semantics stay the engine's.
            EmitUnifyTwo(load2 ?? (() => RegLoad(2)),
                         () => CellLoadDyn(LHeapB, LAtVal), pc);
            Op(new Branch(1));                              // -> $done

            CloseNested();                                  // $slow
            emitBuiltinExit();
            CloseNested();                                  // $done
        }


        private void EmitExecute(Instr ins)
        {
            EmitFlagsCheck(ins.Pc);
            EmitExecuteTail(ins.Pc);
        }

        private void EmitExecuteTail(int pc)
        {
            if (!_callee.TryGetValue(pc, out int callee))
                throw new WasmCompileException($"execute at {pc} has no call site");
            if (_env.TryGetBuiltin(callee, out int builtinId))
            {
                if (_env.IsInlineUnify(builtinId))
                {
                    // Tail =/2: unify, then proceed at Cp.
                    EmitInlineUnify(pc);
                    EmitProceedReturn();
                    return;
                }
                if (_env.TryGetInlineTypeTest(builtinId, out var tailTest))
                {
                    // Tail type test: answer it, then proceed at Cp.
                    EmitInlineTypeTest(tailTest, pc);
                    EmitProceedReturn();
                    return;
                }
                if (_env.IsInlineGetAttr(builtinId))
                {
                    EmitInlineGetAttr(pc, () =>
                    {
                        StoreSlot64(WasmAbi.BuiltinId,
                            () => Op(new Int64Constant(_env.EncodeBuiltinId(builtinId, 0))));
                        StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                        EmitReturn(WasmVerdict.BuiltinRequest);
                    });
                    EmitProceedReturn();
                    return;
                }
                if (_env.IsInlineCompare(builtinId, out bool tNeg))
                {
                    EmitInlineCompare(pc, tNeg, () =>
                    {
                        StoreSlot64(WasmAbi.BuiltinId,
                            () => Op(new Int64Constant(_env.EncodeBuiltinId(builtinId, 0))));
                        StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                        EmitReturn(WasmVerdict.BuiltinRequest);
                    });
                    EmitProceedReturn();
                    return;
                }
                // A builtin in tail position: run it, then proceed. Cursor -1
                // is that convention on the wire.
                StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(_env.EncodeBuiltinId(builtinId, 0))));
                StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                EmitReturn(WasmVerdict.BuiltinRequest);
                return;
            }
            if (_entryByFid.TryGetValue(callee, out int calleeEntry))
            {
                // An in-group tail call: back to the dispatch at the entry,
                // unless the heap crossed the watermark (the engine collects
                // there). Baked for the same reason as EmitCall.
                Op(new LocalGet(LH));
                LoadSlot32(WasmAbi.HeapWatermark);
                Op(new Int32GreaterThanOrEqualSigned());
                OpenIf();
                EmitDeopt(pc, 19);
                CloseNested();
                // A tail call still enters a new procedure: the next
                // iteration's neck_cut must see B as of THIS dispatch, not
                // the barrier the original entry came in with -- a body that
                // left choice points would be over-cut (SetB0(B) parity).
                StoreSlotFromI32Local(WasmAbi.CutBarrier, LB);
                GoTo(calleeEntry);
                return;
            }
            StoreSlotFromI32Local(WasmAbi.CutBarrier, LB);
            EmitForeignCallOrExit(callee, pc);
        }

        // ---- dispatch ----

        private void EmitSwitchOnTerm(int pc, int reg, int varA, int constA, int listA, int structA)
        {
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            TagOfC0(); Op(new LocalSet(LT0));

            // Ref (0) and everything without its own bucket -> the var chain.
            void IfTagGo(int tag, int addr)
            {
                Op(new LocalGet(LT0));
                Op(new Int32Constant(tag));
                Op(new Int32Equal());
                OpenIf();
                GoTo(addr);
                CloseNested();
            }
            IfTagGo((int)Tag.Int, constA);
            IfTagGo((int)Tag.Atom, constA);
            IfTagGo((int)Tag.Float, constA);
            IfTagGo((int)Tag.Lis, listA);
            IfTagGo((int)Tag.Str, structA);
            // A packed string is a cons or [] (ADR-047) -- shapes this slice
            // does not build; step aside rather than guess.
            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Pstr));
            Op(new Int32Equal());
            OpenIf();
            EmitDeopt(pc, 20);
            CloseNested();
            GoTo(varA);
        }

        private void EmitSwitchOnInteger(Instr ins, int reg, int tableId)
        {
            int b = Bias(ins);
            var table = Sec(ins).Predicate.SwitchTables[tableId];
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            TagOfC0();
            Op(new Int32Constant((int)Tag.Int));
            Op(new Int32NotEqual());
            OpenIf();
            GoTo(b + table.DefaultAddress);
            CloseNested();
            // The payload, sign-extended from 60 bits.
            Op(new LocalGet(LC0));
            Op(new Int64Constant(4));
            Op(new Int64ShiftLeft());
            Op(new Int64Constant(4));
            Op(new Int64ShiftRightSigned());
            Op(new LocalSet(LC1));
            for (int k = 0; k < table.Count; k++)
            {
                Op(new LocalGet(LC1));
                Op(new Int64Constant(table.Keys[k]));
                Op(new Int64Equal());
                OpenIf();
                GoTo(b + table.Values[k]);
                CloseNested();
            }
            GoTo(b + table.DefaultAddress);
        }

        private void EmitSwitchOnAtom(Instr ins, int reg, int tableId)
        {
            int b = Bias(ins);
            var table = Sec(ins).Predicate.SwitchTables[tableId];
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            for (int k = 0; k < table.Count; k++)
            {
                Op(new LocalGet(LC0));
                Op(new Int64Constant(_env.AtomCell(table.Keys[k])));
                Op(new Int64Equal());
                OpenIf();
                GoTo(b + table.Values[k]);
                CloseNested();
            }
            GoTo(b + table.DefaultAddress);
        }

        /// <summary>Structure dispatch, the interpreter's semantics exactly:
        /// a Str cell's FUNCTOR CELL (heap[index]) decides the branch; any
        /// other tag falls to the default chain (a Lis was routed by the term
        /// switch already; a mismatch belongs to the default's own tests).</summary>
        private void EmitSwitchOnStructure(Instr ins, int reg, int tableId)
        {
            int b = Bias(ins);
            var table = Sec(ins).Predicate.SwitchTables[tableId];
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            TagOfC0();
            Op(new Int32Constant((int)Tag.Str));
            Op(new Int32NotEqual());
            OpenIf();
            GoTo(b + table.DefaultAddress);
            CloseNested();
            // The functor cell at the Str payload's heap index.
            Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LT1));
            for (int k = 0; k < table.Count; k++)
            {
                CellLoadDyn(LHeapB, LT1);
                Op(new Int64Constant(_env.FunctorCell(table.Keys[k])));
                Op(new Int64Equal());
                OpenIf();
                GoTo(b + table.Values[k]);
                CloseNested();
            }
            GoTo(b + table.DefaultAddress);
        }

        // ---- sub-argument indexing (ADR-027 / ADR-028) ----

        /// <summary>One hop of the bounded sub-path: LC0 holds a dereferenced
        /// cell, and this replaces it with its <paramref name="idx"/>-th
        /// argument, dereferenced. A hop that cannot be taken -- a cell that
        /// is not compound, an index past the arity — is a MISS and jumps to
        /// <paramref name="missAddr"/>, which is the table's default: exactly
        /// what TryHop returning false does in the interpreter.
        ///
        /// <para>A packed string steps aside: its hops read the head element
        /// and the tail through engine helpers that have no wasm counterpart,
        /// and the interpreter re-runs the whole instruction after the deopt
        /// anyway.</para></summary>
        private void EmitSubHop(int idx, int missAddr, int pcForDeopt)
        {
            TagOfC0(); Op(new LocalSet(LT0));

            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Pstr));
            Op(new Int32Equal());
            OpenIf();
            EmitDeopt(pcForDeopt, 20);
            CloseNested();

            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Lis));
            Op(new Int32Equal());
            OpenIf();
            {
                // A cons has exactly two arguments, laid out at its payload.
                if ((uint)idx > 1u) GoTo(missAddr);
                else
                {
                    Op(new LocalGet(LC0)); Op(new Int32WrapInt64());
                    Op(new Int32Constant(idx)); Op(new Int32Add());
                    Op(new LocalSet(LT1));
                    CellLoadDyn(LHeapB, LT1);
                    Op(new LocalSet(LC0));
                    Deref();
                }
            }
            OpenElse();
            {
                Op(new LocalGet(LT0));
                Op(new Int32Constant((int)Tag.Str));
                Op(new Int32NotEqual());
                OpenIf();
                GoTo(missAddr);                 // not compound: a miss
                CloseNested();

                // arity of the functor cell at the payload, from the host's
                // mirror of the functor table: one i64 per id, and the arity
                // is its low half.
                Op(new LocalGet(LC0)); Op(new Int32WrapInt64());
                Op(new LocalSet(LT1));          // the structure's heap index
                LoadSlot32(WasmAbi.FunctorTableBase);
                CellLoadDyn(LHeapB, LT1);
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new Int32WrapInt64());
                Op(new Int32Constant(3)); Op(new Int32ShiftLeft());
                Op(new Int32Add());
                Op(new Int64Load());
                Op(new Int32WrapInt64());
                Op(new Int32Constant(idx));
                Op(new Int32LessThanOrEqualSigned());
                OpenIf();
                GoTo(missAddr);                 // index past the arity: a miss
                CloseNested();

                Op(new LocalGet(LT1));
                Op(new Int32Constant(1 + idx)); Op(new Int32Add());
                Op(new LocalSet(LT1));
                CellLoadDyn(LHeapB, LT1);
                Op(new LocalSet(LC0));
                Deref();
            }
            CloseNested();
        }

        /// <summary>Walks the whole sub-path into LC0 (sub1 &lt; 0 means one
        /// hop), leaving a dereferenced terminal.</summary>
        private void EmitSubWalk(int reg, int sub0, int sub1, int missAddr, int pcForDeopt)
        {
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            EmitSubHop(sub0, missAddr, pcForDeopt);
            if (sub1 >= 0) EmitSubHop(sub1, missAddr, pcForDeopt);
        }

        /// <summary>switch_on_atom_sub / switch_on_integer_sub: the terminal
        /// of the sub-path decides, and a terminal of the wrong type is the
        /// table's default — the cell comparison covers both, since an atom
        /// cell and an integer cell differ in their tag.</summary>
        private void EmitSwitchOnConstSub(Instr ins, bool atoms)
        {
            int b = Bias(ins);
            var table = Sec(ins).Predicate.SwitchTables[ins.I3];
            int miss = b + table.DefaultAddress;
            EmitSubWalk(ins.I0, ins.I1, ins.I2, miss, ins.Pc);
            for (int k = 0; k < table.Count; k++)
            {
                Op(new LocalGet(LC0));
                Op(new Int64Constant(atoms
                    ? _env.AtomCell(table.Keys[k])
                    : Cell.Int(table.Keys[k]).Data));
                Op(new Int64Equal());
                OpenIf();
                GoTo(b + table.Values[k]);
                CloseNested();
            }
            GoTo(miss);
        }

        /// <summary>switch_on_structure_sub: a Str terminal keys by its
        /// functor; a cons keys as './2' (ADR-017 inline lists carry no
        /// functor cell, and ADR-047 makes a non-empty packed string a cons
        /// too — that one steps aside in the hop above).</summary>
        private void EmitSwitchOnStructureSub(Instr ins)
        {
            int b = Bias(ins);
            var table = Sec(ins).Predicate.SwitchTables[ins.I3];
            int miss = b + table.DefaultAddress;
            EmitSubWalk(ins.I0, ins.I1, ins.I2, miss, ins.Pc);

            TagOfC0(); Op(new LocalSet(LT0));
            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Lis));
            Op(new Int32Equal());
            OpenIf();
            {
                // The cons key is the ATOM id of '.', not an interned './2':
                // the interpreter and the IL backend both look it up that way,
                // and a table this one keys differently silently misses.
                bool listed = false;
                for (int k = 0; k < table.Count && !listed; k++)
                    if (table.Keys[k] == AtomTable.ConsFunctorId)
                    { GoTo(b + table.Values[k]); listed = true; }
                if (!listed) GoTo(miss);
            }
            CloseNested();

            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Str));
            Op(new Int32NotEqual());
            OpenIf();
            GoTo(miss);
            CloseNested();

            Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LT1));
            for (int k = 0; k < table.Count; k++)
            {
                CellLoadDyn(LHeapB, LT1);
                Op(new Int64Constant(_env.FunctorCell(table.Keys[k])));
                Op(new Int64Equal());
                OpenIf();
                GoTo(b + table.Values[k]);
                CloseNested();
            }
            GoTo(miss);
        }

        // ---- choice points (the engine's own layout, cell for cell) ----

        private void EmitTry(Instr ins)
            => EmitPushChoicePoint(ins.Pc, SelfFid(ins), ins.I1,
                                   bpAddress: ins.Pc + 9, gotoAddr: Bias(ins) + ins.I0);

        /// <summary>The engine's PushChoicePoint, cell for cell; jumps to
        /// <paramref name="gotoAddr"/> afterwards when one is given, else
        /// falls through.</summary>
        private void EmitPushChoicePoint(int pc, int fid, int arity, int bpAddress,
                                         int gotoAddr = -1)
        {
            int size = 11 + arity;
            Op(new LocalGet(LST));
            Op(new Int32Constant(size));
            Op(new Int32Add());
            LoadSlot32(WasmAbi.StackLimit);
            Op(new Int32GreaterThanSigned());
            OpenIf();
            EmitDeopt(pc, 21);
            CloseNested();

            // newB = ST; the CP words exactly as PushChoicePoint writes them.
            CellStoreDyn(LStackB, LST, 0, () => RawInt(() => Op(new Int32Constant(arity))));
            for (int r = 0; r < arity; r++)
                CellStoreDyn(LStackB, LST, 1 + r, () => RegLoad(r));
            int ctl = 1 + arity;
            CellStoreDyn(LStackB, LST, ctl + 0, () => RawInt(() => Op(new LocalGet(LE))));
            CellStoreDyn(LStackB, LST, ctl + 1, () => RawInt(() => Op(new LocalGet(LCP))));
            CellStoreDyn(LStackB, LST, ctl + 2, () => RawInt(() => Op(new LocalGet(LB))));
            CellStoreDyn(LStackB, LST, ctl + 3, () => RawInt(() =>
                Op(new Int32Constant(_env.EncodeBp(fid, bpAddress)))));
            CellStoreDyn(LStackB, LST, ctl + 4, () => RawInt(() => Op(new LocalGet(LTR))));
            CellStoreDyn(LStackB, LST, ctl + 5, () => RawInt(() => LoadSlot32(WasmAbi.ExtraTrailTop)));
            CellStoreDyn(LStackB, LST, ctl + 6, () => RawInt(() => Op(new LocalGet(LH))));
            CellStoreDyn(LStackB, LST, ctl + 7, () => RawInt(() => Op(new LocalGet(LHB))));
            CellStoreDyn(LStackB, LST, ctl + 8, () =>
            {
                LoadSlot64(WasmAbi.ViewGen);
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
                Op(new Int64Constant(RawIntTag));
                Op(new Int64Or());
            });
            CellStoreDyn(LStackB, LST, ctl + 9, () => RawInt(() => LoadSlot32(WasmAbi.CutBarrier)));

            Op(new LocalGet(LST)); Op(new LocalSet(LB));
            Op(new LocalGet(LST)); Op(new Int32Constant(size)); Op(new Int32Add());
            Op(new LocalSet(LST));
            Op(new LocalGet(LH)); Op(new LocalSet(LHB));
            if (gotoAddr >= 0) GoTo(gotoAddr);
        }

        /// <summary>The shared restore of retry/trust: registers, E, CP, the
        /// trail unwind, H, ViewGen and B0 -- RestoreCommonFromCurrentCp,
        /// cell for cell. Leaves arity in LT0 and the ctl base in LT1.</summary>
        private void EmitRestoreCommon(int pcForDeopt)
        {
            CellLoadDyn(LStackB, LB);
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT0));                          // arity

            // Registers back from the CP (a dynamic count: a small loop).
            Op(new Int32Constant(0)); Op(new LocalSet(LT2));
            OpenBlock();
            OpenLoop();
            Op(new LocalGet(LT2)); Op(new LocalGet(LT0));
            Op(new Int32GreaterThanOrEqualSigned());
            Op(new BranchIf(1));
            // regs[t2] = stack[B + 1 + t2]. A cell is EIGHT bytes: these
            // index cells, not the 4-byte trail entries.
            Op(new LocalGet(LRegsB));
            Op(new LocalGet(LT2)); Op(new Int32Constant(3)); Op(new Int32ShiftLeft());
            Op(new Int32Add());
            CellAddr(LStackB, LB);
            Op(new LocalGet(LT2)); Op(new Int32Constant(3)); Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new Int64Load { Offset = 8 });               // + the arity word
            Op(new Int64Store());
            Op(new LocalGet(LT2)); Op(new Int32Constant(1)); Op(new Int32Add());
            Op(new LocalSet(LT2));
            Op(new Branch(0));
            CloseNested();
            CloseNested();

            // ctlBase (a byte address) = &stack[B] + arity * 8; the loads
            // below carry the +1 cell for the arity word in their offsets.
            CellAddr(LStackB, LB);
            Op(new LocalGet(LT0)); Op(new Int32Constant(3)); Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new LocalSet(LT1));

            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 1 * 8 });
            Op(new Int32WrapInt64()); Op(new LocalSet(LE));         // ctl[0] + arity word
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 2 * 8 });
            Op(new Int32WrapInt64()); Op(new LocalSet(LCP));        // ctl[1]

            // The extra trail cannot be unwound from here; equal tops means
            // there is nothing to unwind, anything else steps aside.
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 6 * 8 });
            Op(new Int32WrapInt64());
            LoadSlot32(WasmAbi.ExtraTrailTop);
            Op(new Int32NotEqual());
            OpenIf();
            // Leave the two operands where the host can read them: a guard
            // that fires on every backtrack is a bug in the comparison, not
            // a real difference, and only the values at the instant it fired
            // tell the two apart.
            StoreSlot64(WasmAbi.DiagA, () =>
            { Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 6 * 8 }); });
            StoreSlot64(WasmAbi.DiagB, () =>
            { LoadSlot32(WasmAbi.ExtraTrailTop); Op(new Int64ExtendInt32Signed()); });
            EmitDeopt(pcForDeopt, 23);
            CloseNested();

            // Unwind the binding trail to the saved top.
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 5 * 8 });
            Op(new Int32WrapInt64()); Op(new LocalSet(LT2));        // target
            OpenBlock();
            OpenLoop();
            Op(new LocalGet(LTR)); Op(new LocalGet(LT2));
            Op(new Int32LessThanOrEqualSigned());
            Op(new BranchIf(1));
            Op(new LocalGet(LTR)); Op(new Int32Constant(1)); Op(new Int32Subtract());
            Op(new LocalSet(LTR));
            // da = trail[TR]; heap[da] = Ref(da) (= da as an i64 cell)
            Op(new LocalGet(LTrailB));
            Op(new LocalGet(LTR)); Op(new Int32Constant(2)); Op(new Int32ShiftLeft());
            Op(new Int32Add());
            Op(new Int32Load());
            Op(new LocalSet(LDa));
            CellStoreDyn(LHeapB, LDa, 0, () =>
            { Op(new LocalGet(LDa)); Op(new Int64ExtendInt32Unsigned()); });
            Op(new Branch(0));
            CloseNested();
            CloseNested();

            // H is about to go BACKWARDS. Cells claimed before this
            // backtrack were still claimed -- time/1 counts allocations, not
            // the top -- so bank the span being discarded before dropping it.
            Op(new LocalGet(LCells));
            Op(new LocalGet(LH));
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 7 * 8 });
            Op(new Int32WrapInt64());
            Op(new Int32Subtract());
            Op(new Int64ExtendInt32Signed());
            Op(new Int64Add());
            Op(new LocalSet(LCells));
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 7 * 8 });
            Op(new Int32WrapInt64()); Op(new LocalSet(LH));         // ctl[6]
            StoreSlot64(WasmAbi.ViewGen, () =>
            {
                Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 9 * 8 });
                Op(new Int64Constant(Cell.PayloadMask));
                Op(new Int64And());
            });
            StoreSlot64(WasmAbi.CutBarrier, () =>
            {
                Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 10 * 8 });
                Op(new Int32WrapInt64());
                Op(new Int64ExtendInt32Signed());
            });
        }

        private void EmitRetry(Instr ins)
        {
            EmitRestoreCommon(ins.Pc);
            Op(new LocalGet(LH)); Op(new LocalSet(LHB));            // AssignHb(H)
            // The CP's BP moves to the next alternative.
            Op(new LocalGet(LT1));
            RawInt(() => Op(new Int32Constant(
                _env.EncodeBp(SelfFid(ins), ins.Pc + 5))));
            Op(new Int64Store { Offset = 4 * 8 });                  // ctl[3]
            GoTo(Bias(ins) + ins.I0);
        }

        private void EmitTrust(Instr ins)
        {
            EmitRestoreCommon(ins.Pc);
            // HB = the CP's saved HB; then the CP is discarded.
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 8 * 8 });
            Op(new Int32WrapInt64()); Op(new LocalSet(LHB));        // ctl[7]
            Op(new LocalGet(LB)); Op(new LocalSet(LT2));            // oldB
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 3 * 8 });
            Op(new Int32WrapInt64()); Op(new LocalSet(LB));         // ctl[2]
            Op(new LocalGet(LT2)); Op(new LocalSet(LST));
            GoTo(Bias(ins) + ins.I0);
        }

        // ---- frames ----

        private void EmitAllocate(Instr ins) => EmitAllocateFrame(ins.I0, ins.Pc);

        private void EmitAllocateFrame(int n, int pc)
        {
            int size = 3 + n;
            Op(new LocalGet(LST));
            Op(new Int32Constant(size));
            Op(new Int32Add());
            LoadSlot32(WasmAbi.StackLimit);
            Op(new Int32GreaterThanSigned());
            OpenIf();
            EmitDeopt(pc, 21);
            CloseNested();

            CellStoreDyn(LStackB, LST, 0, () => RawInt(() => Op(new LocalGet(LE))));
            CellStoreDyn(LStackB, LST, 1, () => RawInt(() => Op(new LocalGet(LCP))));
            CellStoreDyn(LStackB, LST, 2, () => RawInt(() => Op(new Int32Constant(n))));
            for (int i = 0; i < n; i++)
                CellStoreDyn(LStackB, LST, 3 + i, () => Op(new Int64Constant(RawIntTag)));
            Op(new LocalGet(LST)); Op(new LocalSet(LE));
            Op(new LocalGet(LST)); Op(new Int32Constant(size)); Op(new Int32Add());
            Op(new LocalSet(LST));
        }

        private void EmitDeallocate()
        {
            Op(new LocalGet(LE)); Op(new LocalSet(LT0));            // oldE
            CellLoadDyn(LStackB, LT0, 1);
            Op(new Int32WrapInt64()); Op(new LocalSet(LCP));
            CellLoadDyn(LStackB, LT0, 0);
            Op(new Int32WrapInt64()); Op(new LocalSet(LE));

            // The reclamation, exactly as Deallocate does it: only when no
            // choice point protects the popped frame.
            Op(new LocalGet(LB)); Op(new LocalGet(LT0)); Op(new Int32LessThanSigned());
            OpenIf();
            {
                Op(new LocalGet(LST)); Op(new LocalGet(LT0)); Op(new Int32GreaterThanSigned());
                OpenIf();
                {
                    // eTop = E < 0 ? 0 : E + 3 + max(N, 0)
                    Op(new LocalGet(LE)); Op(new Int32Constant(0)); Op(new Int32LessThanSigned());
                    OpenIf();
                    Op(new Int32Constant(0)); Op(new LocalSet(LT1));
                    OpenElse();
                    CellLoadDyn(LStackB, LE, 2);
                    Op(new Int32WrapInt64()); Op(new LocalSet(LT1));
                    Op(new LocalGet(LT1)); Op(new Int32Constant(0)); Op(new Int32LessThanSigned());
                    OpenIf();
                    Op(new Int32Constant(0)); Op(new LocalSet(LT1));
                    CloseNested();
                    Op(new LocalGet(LE)); Op(new Int32Constant(3)); Op(new Int32Add());
                    Op(new LocalGet(LT1)); Op(new Int32Add());
                    Op(new LocalSet(LT1));
                    CloseNested();
                    // bTop = B < 0 ? 0 : B + 11 + arity(B)
                    Op(new LocalGet(LB)); Op(new Int32Constant(0)); Op(new Int32LessThanSigned());
                    OpenIf();
                    Op(new Int32Constant(0)); Op(new LocalSet(LT2));
                    OpenElse();
                    CellLoadDyn(LStackB, LB);
                    Op(new Int32WrapInt64());
                    Op(new Int32Constant(11)); Op(new Int32Add());
                    Op(new LocalGet(LB)); Op(new Int32Add());
                    Op(new LocalSet(LT2));
                    CloseNested();
                    // ST = max(eTop, bTop)
                    Op(new LocalGet(LT1)); Op(new LocalGet(LT2));
                    Op(new LocalGet(LT1)); Op(new LocalGet(LT2));
                    Op(new Int32GreaterThanSigned());
                    Op(new Select());
                    Op(new LocalSet(LST));
                }
                CloseNested();
            }
            CloseNested();
        }

        // ---- the register/Y helpers with unify semantics ----

        /// <summary>Pushes the cell in <paramref name="local"/> as a BIND
        /// value: an attributed variable's cell becomes Ref(home) — its
        /// payload — because an AttVar cell exists only at its home (Deref
        /// does not follow it; a raw copy elsewhere is an orphan the attr
        /// table knows nothing about, and get_attr crashed on one: the
        /// boards.pl clpfd corruption). Everything else passes through.</summary>
        private void PushCellAttVarAsRef(uint local)
        {
            Op(new LocalGet(local)); Op(new LocalSet(LC1));
            Op(new LocalGet(LC1));
            Op(new Int64Constant(60)); Op(new Int64ShiftRightUnsigned());
            Op(new Int64Constant((long)Tag.AttVar)); Op(new Int64Equal());
            OpenIf();
            Op(new LocalGet(LC1));
            Op(new Int64Constant(Cell.PayloadMask)); Op(new Int64And());
            Op(new LocalSet(LC1));
            CloseNested();
            Op(new LocalGet(LC1));
        }

        /// <param name="emitEscape">What to do with a pair only the
        /// engine's unifier can decide. Null steps aside at this pc; the
        /// meta-call passes a builtin-request exit instead, because there
        /// the goal IS =/2 and the host can be asked to run it without
        /// abandoning the chain.</param>
        private void EmitUnifyTwo(Action loadLeft, Action loadRight, int pc,
                                  Action? emitEscape = null)
        {
            // Unifies two cells (get_value_y / get_value_x). Both sides
            // derefed; the general shapes step aside.
            var ins = new Instr(pc, Opcode.GetValueY, 0, 0);   // pc carrier for the emits below
            loadLeft(); Op(new LocalSet(LC0)); Deref();
            Op(new LocalGet(LC0)); Op(new LocalSet(LC2));
            Op(new LocalGet(LDa)); Op(new LocalSet(LT2));           // left-side home
            loadRight(); Op(new LocalSet(LC0)); Deref();

            // Same cell -> done (covers two equal constants and the same var).
            Op(new LocalGet(LC0)); Op(new LocalGet(LC2)); Op(new Int64Equal());
            OpenIf();
            OpenElse();
            {
                // X side unbound. Against a bound Y value, bind X's home
                // to it; against an unbound Y, bind YOUNG to OLD -- the
                // younger home takes the reference, as the engine's unifier
                // does, so backtracking never leaves an old cell pointing at
                // reclaimed heap.
                TagOfC0(); Op(new Int32Constant(0)); Op(new Int32Equal());
                OpenIf();
                {
                    Op(new LocalGet(LC2));
                    Op(new Int64Constant(60));
                    Op(new Int64ShiftRightUnsigned());
                    Op(new Int64Constant(0)); Op(new Int64Equal());
                    OpenIf();
                    {
                        // Both unbound: LDa (X home) vs LT2 (Y home).
                        Op(new LocalGet(LDa)); Op(new LocalGet(LT2));
                        Op(new Int32LessThanSigned());
                        OpenIf();
                        {
                            // Y is younger: Y home -> X home.
                            Op(new LocalGet(LT2)); Op(new LocalSet(LT0));
                            Op(new LocalGet(LDa)); Op(new LocalSet(LT1));
                        }
                        OpenElse();
                        {
                            Op(new LocalGet(LDa)); Op(new LocalSet(LT0));
                            Op(new LocalGet(LT2)); Op(new LocalSet(LT1));
                        }
                        CloseNested();
                        // heap[T0] = Ref(T1); trail T0 when it is old.
                        Op(new LocalGet(LDa));  // scratch: LDa reused below
                        Op(new LocalSet(LT2));
                        Op(new LocalGet(LT0)); Op(new LocalSet(LDa));
                        EmitBindDa(ins.Pc, () =>
                        { Op(new LocalGet(LT1)); Op(new Int64ExtendInt32Unsigned()); });
                    }
                    OpenElse();
                    EmitBindDa(ins.Pc, () => PushCellAttVarAsRef(LC2));
                    CloseNested();
                }
                OpenElse();
                {
                    // Y side unbound -> bind it to the X side's value.
                    Op(new LocalGet(LC2));
                    Op(new Int64Constant(60));
                    Op(new Int64ShiftRightUnsigned());
                    Op(new Int64Constant(0)); Op(new Int64Equal());
                    OpenIf();
                    {
                        Op(new LocalGet(LT2)); Op(new LocalSet(LDa));
                        EmitBindDa(ins.Pc, () => PushCellAttVarAsRef(LC0));
                    }
                    OpenElse();
                    {
                        // Two bound, different cells. An immediate pair (Int
                        // or Atom on both sides) plainly fails; anything else
                        // -- a compound, a float's two-cell shape, an
                        // attributed variable -- steps aside.
                        TagOfC0(); Op(new LocalSet(LT0));
                        Op(new LocalGet(LC2));
                        Op(new Int64Constant(60)); Op(new Int64ShiftRightUnsigned());
                        Op(new Int32WrapInt64());
                        Op(new LocalSet(LT1));
                        void IsImmediate(uint local)
                        {
                            Op(new LocalGet(local));
                            Op(new Int32Constant((int)Tag.Int));
                            Op(new Int32Equal());
                            Op(new LocalGet(local));
                            Op(new Int32Constant((int)Tag.Atom));
                            Op(new Int32Equal());
                            Op(new Int32Or());
                        }
                        IsImmediate(LT0);
                        IsImmediate(LT1);
                        Op(new Int32And());
                        OpenIf();
                        GoFail();
                        CloseNested();
                        // Two bound non-immediates: the general unifier
                        // (module function 1) walks them over a worklist
                        // above the stack top. Returns 0 fail / 1 ok /
                        // 2 deopt; TR is the only scalar it moves, and a
                        // deopt after partial binding is sound -- the
                        // interpreter re-unifies the already-bound prefix
                        // idempotently.
                        StoreSlotFromI32Local(WasmAbi.TrailTop, LTR);
                        StoreSlotFromI32Local(WasmAbi.HeapBacktrack, LHB);
                        StoreSlotFromI32Local(WasmAbi.StackTop, LST);
                        Op(new LocalGet(LC2));
                        Op(new LocalGet(LC0));
                        Op(new LocalGet(0));
                        Op(new WebAssembly.Instructions.Call((uint)UnifierIndex));
                        Op(new LocalSet(LT0));
                        LoadSlot32(WasmAbi.TrailTop); Op(new LocalSet(LTR));
                        Op(new LocalGet(LT0)); Op(new Int32Constant(0)); Op(new Int32Equal());
                        OpenIf();
                        GoFail();
                        CloseNested();
                        Op(new LocalGet(LT0)); Op(new Int32Constant(2)); Op(new Int32Equal());
                        OpenIf();
                        if (emitEscape is null) EmitDeopt(ins.Pc, 25);
                        else emitEscape();
                        CloseNested();
                    }
                    CloseNested();
                }
                CloseNested();
            }
            CloseNested();
        }

        private void EmitPutVariableY(Instr ins)
        {
            EmitFreshHeapVar(ins.Pc);                               // LC0 = Ref(new)
            YStore(ins.I0, () => Op(new LocalGet(LC0)));
            RegStore(ins.I1, () => Op(new LocalGet(LC0)));
        }

        private void EmitPutVariableX(Instr ins)
        {
            EmitFreshHeapVar(ins.Pc);
            RegStore(ins.I0, () => Op(new LocalGet(LC0)));
            RegStore(ins.I1, () => Op(new LocalGet(LC0)));
        }

        /// <summary>heap[H] = Ref(H); LC0 = that cell; H++. Steps aside when
        /// the heap has reached the watermark (the engine grows or collects
        /// there).</summary>
        private void EmitFreshHeapVar(int pc)
        {
            Op(new LocalGet(LH));
            LoadSlot32(WasmAbi.HeapWatermark);
            Op(new Int32GreaterThanOrEqualSigned());
            OpenIf();
            EmitDeopt(pc, 19);
            CloseNested();
            Op(new LocalGet(LH)); Op(new Int64ExtendInt32Unsigned());
            Op(new LocalSet(LC0));
            CellStoreDyn(LHeapB, LH, 0, () => Op(new LocalGet(LC0)));
            Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
            Op(new LocalSet(LH));
        }

        // ---- fused integer arithmetic (ADR-018) ----

        /// <summary>Loads an a_int operand into LC1 as a plain i64, or steps
        /// aside: only the small-integer lane is compiled, exactly the
        /// interpreter's fast path.</summary>
        private void EmitReadIntOperand(int kind, int val, int pc)
        {
            if (kind == 0) { Op(new Int64Constant(val)); Op(new LocalSet(LC1)); return; }
            if (kind == 4) YLoad(val); else RegLoad(val);
            Op(new LocalSet(LC0));
            Deref();
            TagOfC0();
            Op(new Int32Constant((int)Tag.Int));
            Op(new Int32NotEqual());
            OpenIf();
            EmitDeopt(pc, 26);
            CloseNested();
            Op(new LocalGet(LC0));
            Op(new Int64Constant(4));
            Op(new Int64ShiftLeft());
            Op(new Int64Constant(4));
            Op(new Int64ShiftRightSigned());
            Op(new LocalSet(LC1));
        }

        private void EmitAIntCmp(Instr ins)
        {
            int packed = ins.I0;
            int rel = (packed >> 16) & 0xFF;
            // Read into a_eval slots 0 and 1 rather than LC2/LC1: the slots
            // carry the KIND, which is what lets a float operand stay here
            // instead of escalating. The comparison delivers nothing, so no
            // heap cell is needed whichever lane it takes.
            EmitReadNumericOperand(packed & 0xFF, ins.I1, ins.Pc, 0);
            EmitReadNumericOperand((packed >> 8) & 0xFF, ins.I2, ins.Pc, 1);

            AEvalEitherIsFloat(0, 1);
            OpenIf();
            {
                AEvalAsF64(0);
                AEvalAsF64(1);
                Op(rel switch
                {
                    0 => new Float64Equal(),
                    1 => new Float64NotEqual(),
                    2 => (Instruction)new Float64LessThan(),
                    3 => new Float64GreaterThan(),
                    4 => new Float64LessThanOrEqual(),
                    _ => new Float64GreaterThanOrEqual(),
                });
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                OpenIf();
                GoFail();
                CloseNested();
            }
            OpenElse();
            {
                Op(new LocalGet(LA(0)));
                Op(new LocalGet(LA(1)));
                Op(rel switch
                {
                    0 => new Int64Equal(),
                    1 => new Int64NotEqual(),
                    2 => (Instruction)new Int64LessThanSigned(),
                    3 => new Int64GreaterThanSigned(),
                    4 => new Int64LessThanOrEqualSigned(),
                    _ => new Int64GreaterThanOrEqualSigned(),
                });
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                OpenIf();
                GoFail();
                CloseNested();
            }
            CloseNested();
        }

        private void EmitAIntBin(Instr ins)
        {
            int packed = ins.I0;
            int binOp = (packed >> 24) & 0xFF;
            // The a_eval slots carry the KIND, which is what admits a float
            // operand here: this opcode was 56% of clpr's deopts, all of them
            // an operand whose tag was not Int.
            EmitReadNumericOperand(packed & 0xFF, ins.I1, ins.Pc, 0);
            EmitReadNumericOperand((packed >> 8) & 0xFF, ins.I2, ins.Pc, 1);

            AEvalEitherIsFloat(0, 1);
            OpenIf();
            {
                if (binOp == 3)
                {
                    AEvalAsF64(1);
                    Op(new Float64Constant(0.0));
                    Op(new Float64Equal());
                    OpenIf();
                    EmitDeopt(ins.Pc, 26);      // ISO: evaluation_error, not inf
                    CloseNested();
                }
                if (binOp is 0 or 1 or 2 or 3)
                {
                    AEvalAsF64(0);
                    AEvalAsF64(1);
                    Op(binOp switch
                    {
                        0 => new Float64Add(),
                        1 => new Float64Subtract(),
                        2 => new Float64Multiply(),
                        _ => (Instruction)new Float64Divide(),
                    });
                    AEvalStoreF64(0);
                    EmitDeliverFloat(LA(0), (packed >> 16) & 0xFF, ins.I3, ins.Pc);
                }
                else
                {
                    // mod, shifts, bit ops: integer-only, the host's error.
                    EmitDeopt(ins.Pc, 26);
                }
            }
            OpenElse();
            {
            Op(new LocalGet(LA(0))); Op(new LocalSet(LC2));         // a
            Op(new LocalGet(LA(1))); Op(new LocalSet(LC1));         // b
            EmitIntBinCore(binOp, ins.Pc);
            EmitBoxC0IntoC2();
            EmitDeliverInt((packed >> 16) & 0xFF, ins.I3, ins.Pc);
            }
            CloseNested();
        }

        /// <summary>LC2 op LC1 -&gt; LC0, plain i64s, mirroring TryFastBin:
        /// anything outside the 60-bit int lane (overflow, zero divisor, a
        /// non-fast op) deopts and the engine escalates.</summary>
        private void EmitIntBinCore(int op, int pcDeopt)
        {
            void FitsCheckC0(int pc)
            {
                Op(new LocalGet(LC0)); Op(new Int64Constant(Cell.MinInt60));
                Op(new Int64LessThanSigned());
                Op(new LocalGet(LC0)); Op(new Int64Constant(Cell.MaxInt60));
                Op(new Int64GreaterThanSigned());
                Op(new Int32Or());
                OpenIf();
                EmitDeopt(pc, 26);
                CloseNested();
            }

            switch (op)
            {
                case 0:     // Add
                    Op(new LocalGet(LC2)); Op(new LocalGet(LC1)); Op(new Int64Add());
                    Op(new LocalSet(LC0));
                    FitsCheckC0(pcDeopt);
                    break;
                case 1:     // Sub
                    Op(new LocalGet(LC2)); Op(new LocalGet(LC1)); Op(new Int64Subtract());
                    Op(new LocalSet(LC0));
                    FitsCheckC0(pcDeopt);
                    break;
                case 2:     // Mul -- the 64-bit overflow probe, then the 60-bit fit
                    Op(new LocalGet(LC2)); Op(new LocalGet(LC1)); Op(new Int64Multiply());
                    Op(new LocalSet(LC0));
                    Op(new LocalGet(LC2)); Op(new Int64Constant(0)); Op(new Int64NotEqual());
                    Op(new LocalGet(LC2)); Op(new Int64Constant(-1)); Op(new Int64NotEqual());
                    Op(new Int32And());
                    OpenIf();
                    {
                        Op(new LocalGet(LC0)); Op(new LocalGet(LC2)); Op(new Int64DivideSigned());
                        Op(new LocalGet(LC1)); Op(new Int64NotEqual());
                        OpenIf();
                        EmitDeopt(pcDeopt, 26);
                        CloseNested();
                    }
                    CloseNested();
                    FitsCheckC0(pcDeopt);
                    break;
                case 4:     // IntDiv (truncating)
                    Op(new LocalGet(LC1)); Op(new Int64Constant(0)); Op(new Int64Equal());
                    OpenIf();
                    EmitDeopt(pcDeopt, 26);
                    CloseNested();
                    Op(new LocalGet(LC2)); Op(new LocalGet(LC1)); Op(new Int64DivideSigned());
                    Op(new LocalSet(LC0));
                    break;
                case 5:     // Mod (sign of the divisor)
                    Op(new LocalGet(LC1)); Op(new Int64Constant(0)); Op(new Int64Equal());
                    OpenIf();
                    EmitDeopt(pcDeopt, 26);
                    CloseNested();
                    Op(new LocalGet(LC2)); Op(new LocalGet(LC1)); Op(new Int64RemainderSigned());
                    Op(new LocalSet(LC0));
                    Op(new LocalGet(LC0)); Op(new Int64Constant(0)); Op(new Int64NotEqual());
                    Op(new LocalGet(LC0)); Op(new LocalGet(LC1)); Op(new Int64ExclusiveOr());
                    Op(new Int64Constant(0)); Op(new Int64LessThanSigned());
                    Op(new Int32And());
                    OpenIf();
                    Op(new LocalGet(LC0)); Op(new LocalGet(LC1)); Op(new Int64Add());
                    Op(new LocalSet(LC0));
                    CloseNested();
                    break;
                default:
                    EmitDeopt(pcDeopt, 26);
                    Op(new Int64Constant(0)); Op(new LocalSet(LC0));    // unreachable
                    break;
            }
        }

        /// <summary>Boxes the plain i64 in LC0 as an Int cell into LC2.</summary>
        /// <summary>Writes the double whose IEEE bits are in <paramref
        /// name="bitsLocal"/> as the two cells a float term is (header with
        /// the top 4 bits and the paired index, paired cell with the other
        /// 60), at H. Mirrors Cell.MakeFloat, INCLUDING its single zero: a
        /// computed -0.0 stores as 0.0, or writeq, ==/2 and compare/3 would
        /// disagree with the interpreter on a value it can produce
        /// (0.0 * -1.0).</summary>
        private void EmitWriteFloatBitsAtH(uint bitsLocal)
        {
            // Normalise -0.0 -> 0.0 first, in the local itself.
            Op(new LocalGet(bitsLocal));
            Op(new Float64ReinterpretInt64());
            Op(new Float64Constant(0.0));
            Op(new Float64Equal());
            OpenIf();
            Op(new Int64Constant(0));
            Op(new LocalSet(bitsLocal));
            CloseNested();

            CellStoreDyn(LHeapB, LH, 0, () =>
            {
                Op(new Int64Constant((long)Tag.Float << Cell.TagShift));
                Op(new LocalGet(bitsLocal));
                Op(new Int64Constant(60)); Op(new Int64ShiftRightUnsigned());
                Op(new Int64Constant(0xF)); Op(new Int64And());
                Op(new Int64Constant(56)); Op(new Int64ShiftLeft());
                Op(new Int64Or());
                Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                Op(new Int64ExtendInt32Unsigned());
                Op(new Int64Or());
            });
            CellStoreDyn(LHeapB, LH, 1, () =>
            {
                Op(new Int64Constant((long)Tag.Int << Cell.TagShift));
                Op(new LocalGet(bitsLocal));
                Op(new Int64Constant(Cell.PayloadMask)); Op(new Int64And());
                Op(new Int64Or());
            });
        }

        /// <summary>Delivers a FLOAT result: two heap cells and a REF to the
        /// header, the shape put_float leaves. A target that is already BOUND
        /// escalates -- comparing a computed double against a stored one is
        /// the interpreter's judgement to make (NaN alone would need its
        /// rules), and an is/2 into a bound float is rare enough that the
        /// step aside costs nothing.</summary>
        private void EmitDeliverFloat(uint bitsLocal, int tKind, int tVal, int pcDeopt)
        {
            EmitHeapGuard(pcDeopt, 2);
            EmitWriteFloatBitsAtH(bitsLocal);
            void RefToHeader()
            {
                Op(new LocalGet(LH));
                Op(new Int64ExtendInt32Unsigned());
            }
            switch (tKind)
            {
                case 5: RegStore(tVal, RefToHeader); break;
                case 6: YStore(tVal, RefToHeader); break;
                default:
                    if (tKind == 4) YLoad(tVal); else RegLoad(tVal);
                    Op(new LocalSet(LC0));
                    Deref();
                    TagOfC0(); Op(new Int32Constant(0)); Op(new Int32Equal());
                    OpenIf();
                    EmitBindDa(pcDeopt, RefToHeader);
                    OpenElse();
                    EmitDeopt(pcDeopt, 26);
                    CloseNested();
                    break;
            }
            Op(new LocalGet(LH)); Op(new Int32Constant(2)); Op(new Int32Add());
            Op(new LocalSet(LH));
        }

        private void EmitBoxC0IntoC2()
        {
            Op(new LocalGet(LC0));
            Op(new Int64Constant(Cell.PayloadMask));
            Op(new Int64And());
            Op(new Int64Constant((long)Tag.Int << Cell.TagShift));
            Op(new Int64Or());
            Op(new LocalSet(LC2));
        }

        /// <summary>Delivers the boxed result cell in LC2 to the target: store
        /// kinds write it, unify kinds bind an unbound target or compare an
        /// Int one; any other bound shape deopts.</summary>
        private void EmitDeliverInt(int tKind, int tVal, int pcDeopt)
        {
            switch (tKind)
            {
                case 5: RegStore(tVal, () => Op(new LocalGet(LC2))); break;
                case 6: YStore(tVal, () => Op(new LocalGet(LC2))); break;
                default:
                    // unify (4 = Y, 3 = X): against the boxed result.
                    if (tKind == 4) YLoad(tVal); else RegLoad(tVal);
                    Op(new LocalSet(LC0));
                    Deref();
                    Op(new LocalGet(LC0)); Op(new LocalGet(LC2)); Op(new Int64NotEqual());
                    OpenIf();
                    {
                        TagOfC0(); Op(new Int32Constant(0)); Op(new Int32Equal());
                        OpenIf();
                        EmitBindDa(pcDeopt, () => Op(new LocalGet(LC2)));
                        OpenElse();
                        {
                            TagOfC0(); Op(new Int32Constant((int)Tag.Int)); Op(new Int32Equal());
                            OpenIf();
                            GoFail();
                            CloseNested();
                            EmitDeopt(pcDeopt, 26);
                        }
                        CloseNested();
                    }
                    CloseNested();
                    break;
            }
        }
        // ---- structures and lists (ADR-017 inline cells; ADR-019 last-arg
        // nested builds; the RESERVED forms of ADR-020 are rejected) ----

        /// <summary>Steps aside before any mutation when the next
        /// <paramref name="cells"/> heap cells would cross the watermark.</summary>
        private void EmitHeapGuard(int pc, int cells = 1)
        {
            Op(new LocalGet(LH));
            if (cells != 1) { Op(new Int32Constant(cells - 1)); Op(new Int32Add()); }
            LoadSlot32(WasmAbi.HeapWatermark);
            Op(new Int32GreaterThanOrEqualSigned());
            OpenIf();
            EmitDeopt(pc, 19);
            CloseNested();
        }

        /// <summary>heap[LDa] = value, trailed when old (the engine's Bind).
        /// Trail FIRST: its full-trail deopt must fire before any mutation --
        /// a stored bind without its trail entry would survive backtracking,
        /// and the interpreter's re-run would find the var already bound and
        /// never grow the trail (a deopt storm, measured on tak).</summary>
        private void EmitBindDa(int pc, Action value)
        {
            Op(new LocalGet(LDa));
            Op(new LocalGet(LHB));
            Op(new Int32LessThanSigned());
            OpenIf();
            EmitTrailDa(pc);
            CloseNested();
            CellStoreDyn(LHeapB, LDa, 0, value);
        }

        /// <summary>Pushes a cell of the given tag whose payload is LH plus
        /// <paramref name="plus"/>.</summary>
        private void PushTaggedH(Tag tag, int plus = 0)
        {
            Op(new LocalGet(LH));
            if (plus != 0) { Op(new Int32Constant(plus)); Op(new Int32Add()); }
            Op(new Int64ExtendInt32Unsigned());
            Op(new Int64Constant((long)tag << Cell.TagShift));
            Op(new Int64Or());
        }

        private void EmitGetStructure(int functorId, int reg, int pc)
        {
            long functorCell = _env.FunctorCell(functorId);
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            TagOfC0(); Op(new LocalSet(LT0));

            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Str));
            Op(new Int32Equal());
            OpenIf();
            {
                // Match: same functor, read on through the args.
                Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LT1));
                CellLoadDyn(LHeapB, LT1);
                Op(new Int64Constant(functorCell));
                Op(new Int64NotEqual());
                OpenIf();
                GoFail();
                CloseNested();
                Op(new Int32Constant(0)); Op(new LocalSet(LMode));
                Op(new LocalGet(LT1)); Op(new Int32Constant(1)); Op(new Int32Add());
                Op(new LocalSet(LS));
            }
            OpenElse();
            {
                Op(new LocalGet(LT0));
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                OpenIf();
                {
                    // Unbound: build. The functor cell goes at H, the var
                    // binds to STR(H), and the args will be written next.
                    EmitHeapGuard(pc);
                    CellStoreDyn(LHeapB, LH, 0, () => Op(new Int64Constant(functorCell)));
                    EmitBindDa(pc, () => PushTaggedH(Tag.Str));
                    Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                    Op(new LocalSet(LH));
                    Op(new LocalGet(LH)); Op(new LocalSet(LS));
                    Op(new Int32Constant(1)); Op(new LocalSet(LMode));
                }
                OpenElse();
                {
                    Op(new LocalGet(LT0));
                    Op(new Int32Constant((int)Tag.AttVar));
                    Op(new Int32Equal());
                    OpenIf();
                    EmitDeopt(pc, 24);
                    CloseNested();
                    GoFail();
                }
                CloseNested();
            }
            CloseNested();
        }

        private void EmitGetList(int reg, int pc)
        {
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            TagOfC0(); Op(new LocalSet(LT0));

            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Lis));
            Op(new Int32Equal());
            OpenIf();
            {
                Op(new Int32Constant(0)); Op(new LocalSet(LMode));
                Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LS));
            }
            OpenElse();
            {
                Op(new LocalGet(LT0));
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                OpenIf();
                {
                    // Unbound: bind to LIS(H) -- the pair is NOT allocated
                    // here; the two unify_* that follow write it (ADR-017's
                    // two-cell cons).
                    EmitBindDa(pc, () => PushTaggedH(Tag.Lis));
                    Op(new Int32Constant(1)); Op(new LocalSet(LMode));
                    Op(new LocalGet(LH)); Op(new LocalSet(LS));
                }
                OpenElse();
                {
                    // An attributed variable needs its hooks; a packed string
                    // IS a cons but with its own representation. Both step
                    // aside; everything else plainly fails.
                    Op(new LocalGet(LT0));
                    Op(new Int32Constant((int)Tag.AttVar));
                    Op(new Int32Equal());
                    Op(new LocalGet(LT0));
                    Op(new Int32Constant((int)Tag.Pstr));
                    Op(new Int32Equal());
                    Op(new Int32Or());
                    OpenIf();
                    EmitDeopt(pc, 24);
                    CloseNested();
                    GoFail();
                }
                CloseNested();
            }
            CloseNested();
        }

        private void EmitPutStructure(int functorId, int reg, int pc)
        {
            EmitHeapGuard(pc);
            CellStoreDyn(LHeapB, LH, 0,
                () => Op(new Int64Constant(_env.FunctorCell(functorId))));
            RegStore(reg, () => PushTaggedH(Tag.Str));
            Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
            Op(new LocalSet(LH));
            Op(new LocalGet(LH)); Op(new LocalSet(LS));
            Op(new Int32Constant(1)); Op(new LocalSet(LMode));
        }

        /// <summary>unify_variable_*: in write mode a fresh heap variable, in
        /// read mode the cell at S (a bare ATTVAR captured as a REF to its
        /// home, never copied). The callback stores LC0 wherever the operand
        /// says.</summary>
        private void EmitUnifyVariable(int pc, Action write, Action read)
        {
            Op(new LocalGet(LMode));
            OpenIf();
            {
                EmitHeapGuard(pc);
                Op(new LocalGet(LH)); Op(new Int64ExtendInt32Unsigned());
                Op(new LocalSet(LC0));
                CellStoreDyn(LHeapB, LH, 0, () => Op(new LocalGet(LC0)));
                Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                Op(new LocalSet(LH));
                write();
            }
            OpenElse();
            {
                CellLoadDyn(LHeapB, LS);
                Op(new LocalSet(LC0));
                TagOfC0();
                Op(new Int32Constant((int)Tag.AttVar));
                Op(new Int32Equal());
                OpenIf();
                Op(new LocalGet(LS)); Op(new Int64ExtendInt32Unsigned());
                Op(new LocalSet(LC0));
                CloseNested();
                read();
            }
            CloseNested();
            Op(new LocalGet(LS)); Op(new Int32Constant(1)); Op(new Int32Add());
            Op(new LocalSet(LS));
        }

        /// <summary>unify_value_*: in write mode the operand's cell goes onto
        /// the heap (a bare ATTVAR as a REF to its home); in read mode a full
        /// two-cell unify against heap[S].</summary>
        private void EmitUnifyValue(int pc, Action loadSrc)
        {
            Op(new LocalGet(LMode));
            OpenIf();
            {
                EmitHeapGuard(pc);
                loadSrc(); Op(new LocalSet(LC0));
                TagOfC0();
                Op(new Int32Constant((int)Tag.AttVar));
                Op(new Int32Equal());
                OpenIf();
                Op(new LocalGet(LC0));
                Op(new Int64Constant(0xFFFFFFFFL));
                Op(new Int64And());
                Op(new LocalSet(LC0));
                CloseNested();
                CellStoreDyn(LHeapB, LH, 0, () => Op(new LocalGet(LC0)));
                Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                Op(new LocalSet(LH));
            }
            OpenElse();
            {
                EmitUnifyTwo(() =>
                {
                    Op(new LocalGet(LS)); Op(new LocalSet(LDa));
                    CellLoadDyn(LHeapB, LS);
                }, loadSrc, pc);
            }
            CloseNested();
            Op(new LocalGet(LS)); Op(new Int32Constant(1)); Op(new Int32Add());
            Op(new LocalSet(LS));
        }

        private void EmitUnifyConst(long constCell, int pc)
        {
            Op(new LocalGet(LMode));
            OpenIf();
            {
                EmitHeapGuard(pc);
                CellStoreDyn(LHeapB, LH, 0, () => Op(new Int64Constant(constCell)));
                Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                Op(new LocalSet(LH));
            }
            OpenElse();
            {
                Op(new LocalGet(LS)); Op(new LocalSet(LDa));
                CellLoadDyn(LHeapB, LS);
                Op(new LocalSet(LC0));
                Deref();
                UnifyC0WithConst(constCell, pc);
            }
            CloseNested();
            Op(new LocalGet(LS)); Op(new Int32Constant(1)); Op(new Int32Add());
            Op(new LocalSet(LS));
        }

        private void EmitUnifyVoid(int count, int pc)
        {
            Op(new LocalGet(LMode));
            OpenIf();
            {
                EmitHeapGuard(pc, count);
                for (int i = 0; i < count; i++)
                {
                    CellStoreDyn(LHeapB, LH, 0, () =>
                    { Op(new LocalGet(LH)); Op(new Int64ExtendInt32Unsigned()); });
                    Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                    Op(new LocalSet(LH));
                }
            }
            CloseNested();
            Op(new LocalGet(LS)); Op(new Int32Constant(count)); Op(new Int32Add());
            Op(new LocalSet(LS));
        }

        /// <summary>ADR-019's last-argument nested build / match.</summary>
        private void EmitUnifyStructure(int functorId, int pc)
        {
            long functorCell = _env.FunctorCell(functorId);
            Op(new LocalGet(LMode));
            OpenIf();
            {
                // Building: the parent's arg slot takes STR(H+1) and the
                // functor follows -- contiguous, because this is the LAST
                // argument (ADR-019).
                EmitHeapGuard(pc, 2);
                CellStoreDyn(LHeapB, LH, 0, () => PushTaggedH(Tag.Str, 1));
                CellStoreDyn(LHeapB, LH, 1, () => Op(new Int64Constant(functorCell)));
                Op(new LocalGet(LH)); Op(new Int32Constant(2)); Op(new Int32Add());
                Op(new LocalSet(LH));
                Op(new LocalGet(LH)); Op(new LocalSet(LS));
            }
            OpenElse();
            {
                Op(new LocalGet(LS)); Op(new LocalSet(LDa));
                CellLoadDyn(LHeapB, LS);
                Op(new LocalSet(LC0));
                Deref();
                TagOfC0(); Op(new LocalSet(LT0));

                Op(new LocalGet(LT0));
                Op(new Int32Constant((int)Tag.Str));
                Op(new Int32Equal());
                OpenIf();
                {
                    Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LT1));
                    CellLoadDyn(LHeapB, LT1);
                    Op(new Int64Constant(functorCell));
                    Op(new Int64NotEqual());
                    OpenIf();
                    GoFail();
                    CloseNested();
                    Op(new LocalGet(LT1)); Op(new Int32Constant(1)); Op(new Int32Add());
                    Op(new LocalSet(LS));
                }
                OpenElse();
                {
                    Op(new LocalGet(LT0));
                    Op(new Int32Constant(0));
                    Op(new Int32Equal());
                    OpenIf();
                    {
                        EmitHeapGuard(pc);
                        CellStoreDyn(LHeapB, LH, 0, () => Op(new Int64Constant(functorCell)));
                        EmitBindDa(pc, () => PushTaggedH(Tag.Str));
                        Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                        Op(new LocalSet(LH));
                        Op(new LocalGet(LH)); Op(new LocalSet(LS));
                        Op(new Int32Constant(1)); Op(new LocalSet(LMode));
                    }
                    OpenElse();
                    {
                        Op(new LocalGet(LT0));
                        Op(new Int32Constant((int)Tag.AttVar));
                        Op(new Int32Equal());
                        OpenIf();
                        EmitDeopt(pc, 24);
                        CloseNested();
                        GoFail();
                    }
                    CloseNested();
                }
                CloseNested();
            }
            CloseNested();
        }

        private void EmitUnifyList(int pc)
        {
            Op(new LocalGet(LMode));
            OpenIf();
            {
                // Building: the parent's arg slot takes LIS(H+1); the cons
                // cells themselves are written by what follows.
                EmitHeapGuard(pc);
                CellStoreDyn(LHeapB, LH, 0, () => PushTaggedH(Tag.Lis, 1));
                Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                Op(new LocalSet(LH));
                Op(new LocalGet(LH)); Op(new LocalSet(LS));
            }
            OpenElse();
            {
                Op(new LocalGet(LS)); Op(new LocalSet(LDa));
                CellLoadDyn(LHeapB, LS);
                Op(new LocalSet(LC0));
                Deref();
                TagOfC0(); Op(new LocalSet(LT0));

                Op(new LocalGet(LT0));
                Op(new Int32Constant((int)Tag.Lis));
                Op(new Int32Equal());
                OpenIf();
                {
                    Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LS));
                }
                OpenElse();
                {
                    Op(new LocalGet(LT0));
                    Op(new Int32Constant(0));
                    Op(new Int32Equal());
                    OpenIf();
                    {
                        EmitBindDa(pc, () => PushTaggedH(Tag.Lis));
                        Op(new Int32Constant(1)); Op(new LocalSet(LMode));
                        Op(new LocalGet(LH)); Op(new LocalSet(LS));
                    }
                    OpenElse();
                    {
                        Op(new LocalGet(LT0));
                        Op(new Int32Constant((int)Tag.AttVar));
                        Op(new Int32Equal());
                        Op(new LocalGet(LT0));
                        Op(new Int32Constant((int)Tag.Pstr));
                        Op(new Int32Equal());
                        Op(new Int32Or());
                        OpenIf();
                        EmitDeopt(pc, 24);
                        CloseNested();
                        GoFail();
                    }
                    CloseNested();
                }
                CloseNested();
            }
            CloseNested();
        }

        // ---- cut and the me-else chain ----

        /// <summary>Cut to the barrier the callback pushes: B moves down and
        /// nothing else. The engine's Cut also fires setup_call_cleanup
        /// handlers, prunes IL choice points and compacts the trails -- the
        /// first two are host state the wrapper signals through the Flags
        /// word (checked before every cut), and the compaction is a memory
        /// optimisation the wasm skips: a redundant trail entry unwinds into
        /// dead heap, which is harmless.</summary>
        private void EmitCut(Action pushBarrier)
        {
            pushBarrier();
            Op(new LocalSet(LT0));
            // A stale barrier (at or above B) is a no-op, per ISO: the CP the
            // cut meant to commit to is already gone.
            Op(new LocalGet(LT0));
            Op(new LocalGet(LB));
            Op(new Int32LessThanSigned());
            OpenIf();
            Op(new LocalGet(LT0));
            Op(new LocalSet(LB));
            CloseNested();
        }

        private void EmitTryMeElse(Instr ins)
        {
            // ADR-025: a body try_me_else (inline ITE / disjunction) carries a
            // negative arity sentinel -- its CP saves no argument registers.
            int arity = ins.I1 < 0 ? 0 : ins.I1;
            EmitPushChoicePoint(ins.Pc, SelfFid(ins), arity,
                                bpAddress: Bias(ins) + ins.I0);
            // ...and falls through into the first alternative.
        }

        private void EmitRetryMeElse(Instr ins)
        {
            EmitRestoreCommon(ins.Pc);
            Op(new LocalGet(LH)); Op(new LocalSet(LHB));            // AssignHb(H)
            Op(new LocalGet(LT1));
            RawInt(() => Op(new Int32Constant(
                _env.EncodeBp(SelfFid(ins), Bias(ins) + ins.I0))));
            Op(new Int64Store { Offset = 4 * 8 });                  // ctl[3] = BP
            // ...and falls through into this alternative's code.
        }

        private void EmitTrustMe(Instr ins)
        {
            EmitRestoreCommon(ins.Pc);
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 8 * 8 });
            Op(new Int32WrapInt64()); Op(new LocalSet(LHB));        // saved HB
            Op(new LocalGet(LB)); Op(new LocalSet(LT2));            // oldB
            Op(new LocalGet(LT1)); Op(new Int64Load { Offset = 3 * 8 });
            Op(new Int32WrapInt64()); Op(new LocalSet(LB));         // previous B
            Op(new LocalGet(LT2)); Op(new LocalSet(LST));
            // ...and falls through.
        }

        // ---- floats: two cells, the value baked from the literal pool ----

        /// <summary>Writes the float's two cells at H and pushes nothing:
        /// header at H (its payload carries the high 4 bits and H+1), paired
        /// at H+1. The double's bits are compile-time constants; only the
        /// paired index is runtime.</summary>
        private void EmitWriteFloatAtH(double value)
        {
            var (header, paired) = Cell.MakeFloat(value, 0);
            CellStoreDyn(LHeapB, LH, 0, () =>
            {
                // header | (H+1): the baked part has a zero paired index.
                Op(new Int64Constant(header.Data));
                Op(new LocalGet(LH)); Op(new Int32Constant(1)); Op(new Int32Add());
                Op(new Int64ExtendInt32Unsigned());
                Op(new Int64Or());
            });
            CellStoreDyn(LHeapB, LH, 1, () => Op(new Int64Constant(paired.Data)));
        }

        private void EmitGetFloat(Instr fins)
        {
            int literalId = fins.I0, reg = fins.I1, pc = fins.Pc;
            double value = Sec(fins).FloatLiterals![literalId];
            long bits = System.BitConverter.DoubleToInt64Bits(value == 0.0 ? 0.0 : value);

            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            TagOfC0(); Op(new LocalSet(LT0));

            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Float));
            Op(new Int32Equal());
            OpenIf();
            {
                // Reconstruct the bound float's bits and compare: the cells
                // themselves differ per allocation, the double does not.
                Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LT1));
                Op(new LocalGet(LC0));
                Op(new Int64Constant(56)); Op(new Int64ShiftRightUnsigned());
                Op(new Int64Constant(0xF)); Op(new Int64And());
                Op(new Int64Constant(60)); Op(new Int64ShiftLeft());
                CellLoadDyn(LHeapB, LT1);
                Op(new Int64Constant(Cell.PayloadMask)); Op(new Int64And());
                Op(new Int64Or());
                Op(new Int64Constant(bits));
                Op(new Int64NotEqual());
                OpenIf();
                GoFail();
                CloseNested();
            }
            OpenElse();
            {
                Op(new LocalGet(LT0));
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                OpenIf();
                {
                    EmitHeapGuard(pc, 2);
                    EmitWriteFloatAtH(value);
                    // Bind var -> Ref(header), as the engine's unify does.
                    EmitBindDa(pc, () =>
                    { Op(new LocalGet(LH)); Op(new Int64ExtendInt32Unsigned()); });
                    Op(new LocalGet(LH)); Op(new Int32Constant(2)); Op(new Int32Add());
                    Op(new LocalSet(LH));
                }
                OpenElse();
                {
                    Op(new LocalGet(LT0));
                    Op(new Int32Constant((int)Tag.AttVar));
                    Op(new Int32Equal());
                    OpenIf();
                    EmitDeopt(pc, 25);
                    CloseNested();
                    GoFail();
                }
                CloseNested();
            }
            CloseNested();
        }

        private void EmitPutFloat(Instr fins)
        {
            int literalId = fins.I0, reg = fins.I1, pc = fins.Pc;
            // The register takes a REF to the header, as the engine's
            // put_float does.
            EmitHeapGuard(pc, 2);
            EmitWriteFloatAtH(Sec(fins).FloatLiterals![literalId]);
            RegStore(reg, () =>
            { Op(new LocalGet(LH)); Op(new Int64ExtendInt32Unsigned()); });
            Op(new LocalGet(LH)); Op(new Int32Constant(2)); Op(new Int32Add());
            Op(new LocalSet(LH));
        }

        // ---- a_eval: the RPN stack simulated at compile time (ADR-018) ----
        // The engine evaluates is/2 over a ThreadStatic managed stack; here
        // the stack is DEPTH tracked while compiling and the entries live in
        // i64 locals, so a deopt anywhere in the sequence rewinds to the
        // FIRST push -- pushes are read-only, so the interpreter re-runs the
        // whole sequence against its own stack and nothing is double-applied.

        private const int AEvalMaxDepth = 8;
        private int _aevalDepth;
        private int _aevalStart;

        private static uint LA(int k) => (uint)(23 + k);

        /// <summary>The slot's KIND: 0 = the value is a 60-bit int, 1 = it is
        /// the IEEE bits of a double. One local rather than a parallel f64
        /// bank, so a slot costs one extra i32 and the reinterprets are
        /// free.</summary>
        private static uint LAK(int k) => (uint)(33 + k);

        // The attribute probe's own locals, after the kind bank.
        private const uint LAtSlot = 41;    // i32: the slot being probed
        private const uint LAtVal = 42;     // i32: the attribute's heap index
        private const uint LAtKey = 43;     // i64: ((home + 1) << 32) | module
        private const uint LMetaBase = 44;  // i32: the meta cache's base
        private const uint LMetaGuard = 45; // i32: the probe's bound
        private const uint LMetaArity = 46; // i32: the GOAL's arity, or -1
        private const uint LMetaBarrier = 47;  // i64: '$call'/2's carried barrier
        private const uint LGoalBase = 48;  // i32: the meta-called goal's heap index
        private const uint LGoalCell = 49;  // i64: that goal's functor cell
        private const uint LDomA = 50;      // i64: $dom_same's first cell
        private const uint LDomB = 51;      // i64: $dom_same's second cell
        private const uint LDomBase2 = 52;  // i32: the second domain's heap index
        private const uint LDomI = 53;      // i32: the copy cursor
        private const uint LDomJ = 54;      // i32: the second copy cursor
        private const uint LT0Alt = 55;     // i32: scratch the scan's LT0 outlives
        private const uint LMetaAtom = 56; // i32: 1 when the goal is an atom

        // `!` as an atom. Compared as a relocatable atom CELL, never as a
        // baked id: atom ids are per process.
        private static readonly int CutAtomId =
            Shumway.Core.AtomTable.Intern("!", permanent: true).Id;

        // ADR-051's two names, for the same reason and through the same
        // relocatable cells: a domain is '$fd_dom'(...) of some even arity,
        // and the empty one is an atom of its own.
        private static readonly int FdDomAtomId =
            Shumway.Core.AtomTable.Intern("$fd_dom", permanent: true).Id;

        private static readonly int FdDomEmptyAtomId =
            Shumway.Core.AtomTable.Intern("$fd_dom_empty", permanent: true).Id;

        /// <summary>Pushes the f64 on the wasm stack for slot <paramref
        /// name="k"/>, converting from the int lane when that is what it
        /// holds. The caller has already established that SOME operand is a
        /// float, so this is the promotion the standard requires.</summary>
        private void AEvalAsF64(int k)
        {
            Op(new LocalGet(LAK(k)));
            OpenIf(BlockType.Float64);
            Op(new LocalGet(LA(k)));
            Op(new Float64ReinterpretInt64());
            OpenElse();
            Op(new LocalGet(LA(k)));
            Op(new Float64ConvertInt64Signed());
            CloseNested();
        }

        /// <summary>Stores the f64 on the wasm stack into slot <paramref
        /// name="k"/> as float bits.</summary>
        private void AEvalStoreF64(int k)
        {
            Op(new Int64ReinterpretFloat64());
            Op(new LocalSet(LA(k)));
            Op(new Int32Constant(1));
            Op(new LocalSet(LAK(k)));
        }

        /// <summary>True on the wasm stack when either slot holds a float.</summary>
        private void AEvalEitherIsFloat(int a, int b)
        {
            Op(new LocalGet(LAK(a)));
            Op(new LocalGet(LAK(b)));
            Op(new Int32Or());
        }

        /// <summary>Decodes the FLOAT cell in LC0 into its IEEE bits on the
        /// wasm stack: the header carries the top 4 bits and the paired
        /// cell's payload the other 60 (see Cell.MakeFloat).</summary>
        private void EmitDecodeFloatBitsFromC0()
        {
            Op(new LocalGet(LC0)); Op(new Int32WrapInt64()); Op(new LocalSet(LT1));
            Op(new LocalGet(LC0));
            Op(new Int64Constant(56)); Op(new Int64ShiftRightUnsigned());
            Op(new Int64Constant(0xF)); Op(new Int64And());
            Op(new Int64Constant(60)); Op(new Int64ShiftLeft());
            CellLoadDyn(LHeapB, LT1);
            Op(new Int64Constant(Cell.PayloadMask)); Op(new Int64And());
            Op(new Int64Or());
        }

        private void EmitAEvalPush(Instr ins)
        {
            if (_aevalDepth == 0) _aevalStart = ins.Pc;
            if (_aevalDepth >= AEvalMaxDepth)
                throw new WasmCompileException($"a_eval deeper than {AEvalMaxDepth} at {ins.Pc}");
            switch (ins.I0)
            {
                case 0:
                    Op(new Int64Constant(ins.I1));
                    Op(new LocalSet(LA(_aevalDepth)));
                    Op(new Int32Constant(0));
                    Op(new LocalSet(LAK(_aevalDepth)));
                    break;
                case 2:
                {
                    // A float LITERAL is a constant: its bits go straight in.
                    // This was an unconditional deopt, and it is the site the
                    // measurement found -- 2,000 deopts in a 2,000-iteration
                    // loop over `N * 1.5`, one per iteration.
                    double lit = Sec(ins).FloatLiterals![ins.I1];
                    if (lit == 0.0) lit = 0.0;      // ISO's single zero
                    Op(new Int64Constant(System.BitConverter.DoubleToInt64Bits(lit)));
                    Op(new LocalSet(LA(_aevalDepth)));
                    Op(new Int32Constant(1));
                    Op(new LocalSet(LAK(_aevalDepth)));
                    break;
                }
                case 3:
                case 4:
                    EmitReadNumericOperand(ins.I0, ins.I1, _aevalStart, _aevalDepth);
                    break;
                default:
                    // bigint / rational literal: the sequence still escalates.
                    EmitDeopt(_aevalStart, 26);
                    Op(new Int64Constant(0));                       // unreachable
                    Op(new LocalSet(LA(_aevalDepth)));
                    Op(new Int32Constant(0));
                    Op(new LocalSet(LAK(_aevalDepth)));
                    break;
            }
            _aevalDepth++;
        }

        /// <summary>Reads an a_eval operand into slot <paramref name="slot"/>
        /// with its kind: an INT cell takes the 60-bit lane, a FLOAT cell
        /// decodes to IEEE bits, anything else deopts. The int path is byte
        /// for byte what EmitReadIntOperand emits, so integer arithmetic is
        /// untouched.</summary>
        private void EmitReadNumericOperand(int kind, int val, int pc, int slot)
        {
            if (kind == 0)
            {
                // A 32-bit integer LITERAL, not a register: the fused
                // forms pass these, and reading it as a register number
                // is how this first went wrong.
                Op(new Int64Constant(val));
                Op(new LocalSet(LA(slot)));
                Op(new Int32Constant(0));
                Op(new LocalSet(LAK(slot)));
                return;
            }
            if (kind == 4) YLoad(val); else RegLoad(val);
            Op(new LocalSet(LC0));
            Deref();
            TagOfC0();
            Op(new LocalSet(LT0));

            Op(new LocalGet(LT0));
            Op(new Int32Constant((int)Tag.Int));
            Op(new Int32Equal());
            OpenIf();
            {
                Op(new LocalGet(LC0));
                Op(new Int64Constant(4));
                Op(new Int64ShiftLeft());
                Op(new Int64Constant(4));
                Op(new Int64ShiftRightSigned());
                Op(new LocalSet(LA(slot)));
                Op(new Int32Constant(0));
                Op(new LocalSet(LAK(slot)));
            }
            OpenElse();
            {
                Op(new LocalGet(LT0));
                Op(new Int32Constant((int)Tag.Float));
                Op(new Int32Equal());
                OpenIf();
                {
                    EmitDecodeFloatBitsFromC0();
                    Op(new LocalSet(LA(slot)));
                    Op(new Int32Constant(1));
                    Op(new LocalSet(LAK(slot)));
                }
                OpenElse();
                {
                    // var, bigint, rational, anything else: the interpreter's.
                    EmitDeopt(pc, 26);
                    Op(new Int64Constant(0));
                    Op(new LocalSet(LA(slot)));
                    Op(new Int32Constant(0));
                    Op(new LocalSet(LAK(slot)));
                }
                CloseNested();
            }
            CloseNested();
        }

        private void EmitAEvalBin(Instr ins)
        {
            if (_aevalDepth < 2)
                throw new WasmCompileException($"a_eval_bin underflow at {ins.Pc}");
            int hi = _aevalDepth - 1, lo = _aevalDepth - 2;
            AEvalEitherIsFloat(lo, hi);
            OpenIf();
            {
                // f64: no overflow lane and no 60-bit fit to check. Division
                // by zero is the one case that still belongs to the host --
                // ISO says evaluation_error, not an infinity.
                if (ins.I0 == 3)
                {
                    AEvalAsF64(hi);
                    Op(new Float64Constant(0.0));
                    Op(new Float64Equal());
                    OpenIf();
                    EmitDeopt(_aevalStart, 26);
                    CloseNested();
                }
                if (ins.I0 is 0 or 1 or 2 or 3)
                {
                    AEvalAsF64(lo);
                    AEvalAsF64(hi);
                    Op(ins.I0 switch
                    {
                        0 => new Float64Add(),
                        1 => new Float64Subtract(),
                        2 => new Float64Multiply(),
                        _ => (Instruction)new Float64Divide(),
                    });
                    AEvalStoreF64(lo);
                }
                else
                {
                    // Integer-only operators (mod, shifts, bit ops) on a
                    // float are a type error the host reports.
                    EmitDeopt(_aevalStart, 26);
                }
            }
            OpenElse();
            {
                Op(new LocalGet(LA(lo))); Op(new LocalSet(LC2));
                Op(new LocalGet(LA(hi))); Op(new LocalSet(LC1));
                EmitIntBinCore(ins.I0, _aevalStart);
                Op(new LocalGet(LC0));
                Op(new LocalSet(LA(lo)));
                Op(new Int32Constant(0));
                Op(new LocalSet(LAK(lo)));
            }
            CloseNested();
            _aevalDepth--;
        }

        private void EmitAEvalUn(Instr ins)
        {
            if (_aevalDepth < 1)
                throw new WasmCompileException($"a_eval_un underflow at {ins.Pc}");
            uint a = LA(_aevalDepth - 1);
            int slot = _aevalDepth - 1;

            Op(new LocalGet(LAK(slot)));
            OpenIf();
            {
                switch (ins.I0)
                {
                    case 0:     // Neg
                        AEvalAsF64(slot);
                        Op(new Float64Negate());
                        AEvalStoreF64(slot);
                        break;
                    case 1:     // Pos -- identity
                        break;
                    case 2:     // Abs
                        AEvalAsF64(slot);
                        Op(new Float64Absolute());
                        AEvalStoreF64(slot);
                        break;
                    case 3:     // Sign: -1.0 / 0.0 / 1.0, a FLOAT for a float
                        AEvalAsF64(slot);
                        Op(new Float64Constant(0.0));
                        Op(new Float64GreaterThan());
                        OpenIf(BlockType.Float64);
                        Op(new Float64Constant(1.0));
                        OpenElse();
                        AEvalAsF64(slot);
                        Op(new Float64Constant(0.0));
                        Op(new Float64LessThan());
                        OpenIf(BlockType.Float64);
                        Op(new Float64Constant(-1.0));
                        OpenElse();
                        Op(new Float64Constant(0.0));
                        CloseNested();
                        CloseNested();
                        AEvalStoreF64(slot);
                        break;
                    default:    // bit ops and the transcendentals: the host's
                        EmitDeopt(_aevalStart, 26);
                        break;
                }
            }
            OpenElse();
            {

            void FitsCheck()
            {
                Op(new LocalGet(a)); Op(new Int64Constant(Cell.MinInt60));
                Op(new Int64LessThanSigned());
                Op(new LocalGet(a)); Op(new Int64Constant(Cell.MaxInt60));
                Op(new Int64GreaterThanSigned());
                Op(new Int32Or());
                OpenIf();
                EmitDeopt(_aevalStart, 26);
                CloseNested();
            }

            switch (ins.I0)
            {
                case 0:     // Neg
                    Op(new Int64Constant(0)); Op(new LocalGet(a)); Op(new Int64Subtract());
                    Op(new LocalSet(a));
                    FitsCheck();
                    break;
                case 1:     // Pos -- identity
                    break;
                case 2:     // Abs
                    Op(new LocalGet(a)); Op(new Int64Constant(0));
                    Op(new Int64LessThanSigned());
                    OpenIf();
                    Op(new Int64Constant(0)); Op(new LocalGet(a)); Op(new Int64Subtract());
                    Op(new LocalSet(a));
                    CloseNested();
                    FitsCheck();
                    break;
                case 3:     // Sign
                    Op(new LocalGet(a)); Op(new Int64Constant(0));
                    Op(new Int64GreaterThanSigned());
                    Op(new LocalGet(a)); Op(new Int64Constant(0));
                    Op(new Int64LessThanSigned());
                    Op(new Int32Subtract());
                    Op(new Int64ExtendInt32Signed());
                    Op(new LocalSet(a));
                    break;
                case 4:     // BitNot
                    Op(new LocalGet(a)); Op(new Int64Constant(-1)); Op(new Int64ExclusiveOr());
                    Op(new LocalSet(a));
                    FitsCheck();
                    break;
                default:    // transcendental / float-producing: escalate
                    EmitDeopt(_aevalStart, 26);
                    break;
            }
            }
            CloseNested();
        }

        private void EmitAEvalIs(Instr ins)
        {
            if (_aevalDepth != 1)
                throw new WasmCompileException($"a_eval_is at depth {_aevalDepth} at {ins.Pc}");
            Op(new LocalGet(LAK(0)));
            OpenIf();
            {
                EmitDeliverFloat(LA(0), ins.I0, ins.I1, _aevalStart);
            }
            OpenElse();
            {
                Op(new LocalGet(LA(0))); Op(new LocalSet(LC0));
                EmitBoxC0IntoC2();
                EmitDeliverInt(ins.I0, ins.I1, _aevalStart);
            }
            CloseNested();
            _aevalDepth = 0;
        }

        private void EmitAEvalCmp(Instr ins)
        {
            if (_aevalDepth != 2)
                throw new WasmCompileException($"a_eval_cmp at depth {_aevalDepth} at {ins.Pc}");
            AEvalEitherIsFloat(0, 1);
            OpenIf();
            {
                AEvalAsF64(0);
                AEvalAsF64(1);
                Op(ins.I0 switch
                {
                    0 => new Float64Equal(),
                    1 => new Float64NotEqual(),
                    2 => (Instruction)new Float64LessThan(),
                    3 => new Float64GreaterThan(),
                    4 => new Float64LessThanOrEqual(),
                    _ => new Float64GreaterThanOrEqual(),
                });
                Op(new Int32Constant(0));
                Op(new Int32Equal());
                OpenIf();
                GoFail();
                CloseNested();
            }
            OpenElse();
            {
            Op(new LocalGet(LA(0)));
            Op(new LocalGet(LA(1)));
            Op(ins.I0 switch
            {
                0 => new Int64Equal(),
                1 => new Int64NotEqual(),
                2 => (Instruction)new Int64LessThanSigned(),
                3 => new Int64GreaterThanSigned(),
                4 => new Int64LessThanOrEqualSigned(),
                _ => new Int64GreaterThanOrEqualSigned(),
            });
            Op(new Int32Constant(0));
            Op(new Int32Equal());
            OpenIf();
            GoFail();
            CloseNested();
            }
            CloseNested();
            _aevalDepth = 0;
        }

        // ---- ADR-020 reserved builds, simulated at compile time ----
        // The engine runs put_structure_r / put_list_r with a runtime
        // write-frame stack (PushWriteFrame / OnReservedArgWritten). The
        // build tree is static, so the whole cascade is replayed HERE and
        // the region flattens to one upfront heap guard plus straight
        // stores at fixed offsets from the region base H0. Deopt-free by
        // construction: reserved builds are pure writes, and the guard
        // runs before any mutation, so the interpreter can re-run the
        // region from its first instruction.

        /// <summary>Emits the reserved-build region starting at
        /// <paramref name="startIndex"/>; returns the index of the first
        /// instruction after it.</summary>
        private int EmitReservedRegion(int startIndex)
        {
            var first = _instrs[startIndex];
            var actions = new List<Action>();
            var frames = new List<(int Resume, int Remaining)>();
            int total, writePos;

            // H0-relative pushers.
            void PushCellAt(int off)    // (i64) H0 + off, i.e. Ref/UnboundVar
            {
                Op(new LocalGet(LH));
                if (off != 0) { Op(new Int32Constant(off)); Op(new Int32Add()); }
                Op(new Int64ExtendInt32Unsigned());
            }
            void PushTagged(int off, Tag tag)
            {
                PushCellAt(off);
                Op(new Int64Constant((long)tag << Cell.TagShift));
                Op(new Int64Or());
            }
            void StoreConst(int off, long data)
                => actions.Add(() => CellStoreDyn(LHeapB, LH, off, () => Op(new Int64Constant(data))));

            // The engine's UnifyArgCell copy: a bare ATTVAR cell is captured
            // as a REF to its home so its identity survives.
            void StoreCopy(int off, Action load)
                => actions.Add(() => CellStoreDyn(LHeapB, LH, off, () =>
                {
                    load(); Op(new LocalSet(LC0));
                    TagOfC0(); Op(new Int32Constant((int)Tag.AttVar)); Op(new Int32Equal());
                    OpenIf();
                    Op(new LocalGet(LC0));
                    Op(new Int64Constant(Cell.PayloadMask)); Op(new Int64And());
                    Op(new LocalSet(LC0));
                    CloseNested();
                    Op(new LocalGet(LC0));
                }));

            void FreshVar(int off, Action<Action> target)
            {
                actions.Add(() => CellStoreDyn(LHeapB, LH, off, () => PushCellAt(off)));
                actions.Add(() => target(() => PushCellAt(off)));
            }

            // OnReservedArgWritten, replayed.
            bool Advance()
            {
                writePos++;
                var top = frames[^1];
                frames[^1] = (top.Resume, top.Remaining - 1);
                while (frames.Count > 0 && frames[^1].Remaining == 0)
                {
                    int resume = frames[^1].Resume;
                    frames.RemoveAt(frames.Count - 1);
                    if (frames.Count > 0) writePos = resume;
                    else return true;                       // build complete
                }
                return false;
            }

            // Seed from the entry form.
            if (first.Op == Opcode.PutStructureR)
            {
                int fid = first.I0, reg = first.I1 & 0xFFFFFF, argc = first.I1 >> 24;
                StoreConst(0, _env.FunctorCell(fid));
                actions.Add(() => RegStore(reg, () => PushTagged(0, Tag.Str)));
                writePos = 1; total = argc + 1;
                frames.Add((0, argc));
            }
            else    // PutListR
            {
                int reg = first.I0;
                actions.Add(() => RegStore(reg, () => PushTagged(0, Tag.Lis)));
                writePos = 0; total = 2;
                frames.Add((0, 2));
            }

            int i = startIndex + 1;
            bool done = false;
            while (!done)
            {
                if (i >= _instrs.Count)
                    throw new WasmCompileException($"unterminated reserved build at {first.Pc}");
                var ins = _instrs[i];
                // A leader is an external re-entry point; a half-simulated
                // build cannot be resumed there.
                if (_cursorByAddr.ContainsKey(ins.Pc))
                    throw new WasmCompileException($"leader inside reserved build at {ins.Pc}");
                switch (ins.Op)
                {
                    case Opcode.UnifyAtom:
                    case Opcode.UnifyConstant:
                        StoreConst(writePos, _env.AtomCell(ins.I0)); done = Advance(); break;
                    case Opcode.UnifyInteger:
                        StoreConst(writePos, Cell.Int(ins.I0).Data); done = Advance(); break;
                    case Opcode.UnifyNil:
                        StoreConst(writePos, _env.AtomCell(AtomTable.EmptyListId));
                        done = Advance(); break;
                    case Opcode.UnifyVariableX:
                    {
                        int slot = ins.I0;
                        FreshVar(writePos, v => RegStore(slot, v));
                        done = Advance(); break;
                    }
                    case Opcode.UnifyVariableY:
                    {
                        int slot = ins.I0;
                        FreshVar(writePos, v => YStore(slot, v));
                        done = Advance(); break;
                    }
                    case Opcode.UnifyValueX:
                    {
                        int slot = ins.I0;
                        StoreCopy(writePos, () => RegLoad(slot));
                        done = Advance(); break;
                    }
                    case Opcode.UnifyValueY:
                    {
                        int slot = ins.I0;
                        StoreCopy(writePos, () => YLoad(slot));
                        done = Advance(); break;
                    }
                    case Opcode.UnifyVoid:
                        for (int k = 0; k < ins.I0; k++)
                        {
                            actions.Add(MakeSelfRef(writePos));
                            done = Advance();
                            if (done && k != ins.I0 - 1)
                                throw new WasmCompileException($"unify_void overruns the build at {ins.Pc}");
                        }
                        break;
                    case Opcode.UnifyStructure:
                    {
                        var (_, arity) = FunctorTable.Lookup(ins.I0);
                        int nested = total;
                        StoreConst(nested, _env.FunctorCell(ins.I0));
                        int slotOff = writePos;
                        actions.Add(() => CellStoreDyn(LHeapB, LH, slotOff,
                            () => PushTagged(nested, Tag.Str)));
                        var top = frames[^1];
                        frames[^1] = (top.Resume, top.Remaining - 1);    // no cascade here
                        frames.Add((writePos + 1, arity));
                        writePos = nested + 1;
                        total += arity + 1;
                        break;
                    }
                    case Opcode.UnifyList:
                    {
                        int pair = total;
                        int slotOff = writePos;
                        actions.Add(() => CellStoreDyn(LHeapB, LH, slotOff,
                            () => PushTagged(pair, Tag.Lis)));
                        var top = frames[^1];
                        frames[^1] = (top.Resume, top.Remaining - 1);
                        frames.Add((writePos + 1, 2));
                        writePos = pair;
                        total += 2;
                        break;
                    }
                    default:
                        throw new WasmCompileException($"{ins.Op} inside reserved build at {ins.Pc}");
                }
                i++;
            }

            EmitHeapGuard(first.Pc, total);
            foreach (var a in actions) a();
            Op(new LocalGet(LH)); Op(new Int32Constant(total)); Op(new Int32Add());
            Op(new LocalSet(LH));
            return i;

            Action MakeSelfRef(int off) => () =>
                CellStoreDyn(LHeapB, LH, off, () =>
                {
                    Op(new LocalGet(LH));
                    if (off != 0) { Op(new Int32Constant(off)); Op(new Int32Add()); }
                    Op(new Int64ExtendInt32Unsigned());
                });
        }

        // ---- the general unifier: module function 1 ----
        // (a: i64, b: i64, mailbox: i32) -> i32: 0 fail, 1 ok, 2 deopt.
        // Iterative over a worklist of cell pairs laid above the stack top
        // (nothing pushes frames or CPs while it runs); functor arities come
        // from the host-mirrored table at FunctorTableBase, because the
        // functor table is managed state. Attvars, bigints, rationals and
        // PSTRs deopt: their unification is engine logic. A deopt after
        // partial binding is sound -- everything bound so far was required,
        // is trailed, and re-unifies idempotently when the interpreter
        // re-runs the instruction.

        private static List<Instruction> BuildUnifierBody()
        {
            const uint PA = 0, PB = 1, MB = 2;
            const uint HEAPB = 3, TRAILB = 4, FTABB = 5, TR = 6, HHB = 7,
                       TRLIM = 8, WL = 9, WLBASE = 10, WLLIM = 11,
                       DA = 12, DB = 13, K = 14;
            const uint CA = 15, CB = 16, C1 = 17, FA = 18;

            var code = new List<Instruction>();
            int depth = 0;
            void O(Instruction x) => code.Add(x);
            void LG(uint n) => O(new LocalGet(n));
            void LSet(uint n) => O(new LocalSet(n));
            void I32(int v) => O(new Int32Constant(v));
            void I64(long v) => O(new Int64Constant(v));
            void OIf() { O(new If(BlockType.Empty)); depth++; }
            void OElse() => O(new Else());
            void OEnd() { O(new End()); depth--; }
            void OBlock() { O(new Block(BlockType.Empty)); depth++; }
            void OLoop() { O(new Loop(BlockType.Empty)); depth++; }
            // br to the main loop: every label opened since it sits between.
            void Continue() => O(new Branch((uint)(depth - 1)));

            void SlotToI32(int slot, uint local)
            {
                LG(MB); O(new Int64Load { Offset = WasmAbi.ByteOffset(slot) });
                O(new Int32WrapInt64()); LSet(local);
            }
            void Ret(int verdict)
            {
                LG(MB); LG(TR); O(new Int64ExtendInt32Signed());
                O(new Int64Store { Offset = WasmAbi.ByteOffset(WasmAbi.TrailTop) });
                I32(verdict); O(new Return());
            }
            void HeapLoad(uint idxLocal)
            {
                LG(HEAPB); LG(idxLocal); I32(3); O(new Int32ShiftLeft());
                O(new Int32Add()); O(new Int64Load());
            }
            void TagIs(uint cel, long tag)
            {
                LG(cel); I64(60); O(new Int64ShiftRightUnsigned());
                I64(tag); O(new Int64Equal());
            }
            void Deref(uint cel, uint home)
            {
                OBlock(); OLoop();
                LG(cel); I64(60); O(new Int64ShiftRightUnsigned());
                I64(0); O(new Int64NotEqual()); O(new BranchIf(1));
                LG(cel); O(new Int32WrapInt64()); LSet(home);
                HeapLoad(home); LSet(C1);
                LG(C1); LG(cel); O(new Int64Equal()); O(new BranchIf(1));
                LG(C1); LSet(cel); O(new Branch(0));
                OEnd(); OEnd();
            }
            void HeapStore(uint addr, uint val)
            {
                LG(HEAPB); LG(addr); I32(3); O(new Int32ShiftLeft());
                O(new Int32Add()); LG(val); O(new Int64Store());
            }
            void Bind(uint addr, uint val)
            {
                LG(addr); LG(HHB); O(new Int32LessThanSigned());
                OIf();
                {
                    // Trail space FIRST: a heap store without its trail
                    // entry would survive backtracking.
                    LG(TR); LG(TRLIM); O(new Int32GreaterThanOrEqualSigned());
                    OIf(); Ret(2); OEnd();
                    HeapStore(addr, val);
                    LG(TRAILB); LG(TR); I32(2); O(new Int32ShiftLeft());
                    O(new Int32Add()); LG(addr); O(new Int32Store());
                    LG(TR); I32(1); O(new Int32Add()); LSet(TR);
                }
                OElse();
                HeapStore(addr, val);
                OEnd();
            }
            void PushPairSlot(uint idxLocal, int plus, uint atOffset)
            {
                LG(WL);
                LG(idxLocal);
                if (plus != 0) { I32(plus); O(new Int32Add()); }
                O(new Int64ExtendInt32Unsigned());
                O(new Int64Store { Offset = atOffset });
            }

            // ---- prologue ----
            SlotToI32(WasmAbi.HeapBase, HEAPB);
            SlotToI32(WasmAbi.BindingTrailBase, TRAILB);
            SlotToI32(WasmAbi.FunctorTableBase, FTABB);
            SlotToI32(WasmAbi.TrailTop, TR);
            SlotToI32(WasmAbi.HeapBacktrack, HHB);
            SlotToI32(WasmAbi.TrailLimit, TRLIM);
            SlotToI32(WasmAbi.StackBase, DA);            // scratch
            LG(DA);
            LG(MB); O(new Int64Load { Offset = WasmAbi.ByteOffset(WasmAbi.StackTop) });
            O(new Int32WrapInt64()); I32(3); O(new Int32ShiftLeft());
            O(new Int32Add()); LSet(WLBASE);
            LG(DA);
            LG(MB); O(new Int64Load { Offset = WasmAbi.ByteOffset(WasmAbi.StackLimit) });
            O(new Int32WrapInt64()); I32(3); O(new Int32ShiftLeft());
            O(new Int32Add()); LSet(WLLIM);
            LG(WLBASE); LSet(WL);
            LG(WL); I32(16); O(new Int32Add()); LG(WLLIM);
            O(new Int32GreaterThanSigned());
            OIf(); Ret(2); OEnd();
            LG(WL); LG(PA); O(new Int64Store());
            LG(WL); LG(PB); O(new Int64Store { Offset = 8 });
            LG(WL); I32(16); O(new Int32Add()); LSet(WL);

            // ---- main loop ----
            OLoop();
            {
                LG(WL); LG(WLBASE); O(new Int32Equal());
                OIf(); Ret(1); OEnd();
                LG(WL); I32(16); O(new Int32Subtract()); LSet(WL);
                LG(WL); O(new Int64Load()); LSet(CA);
                LG(WL); O(new Int64Load { Offset = 8 }); LSet(CB);
                Deref(CA, DA);
                Deref(CB, DB);
                LG(CA); LG(CB); O(new Int64Equal());
                OIf(); Continue(); OEnd();

                TagIs(CA, (long)Tag.AttVar); TagIs(CB, (long)Tag.AttVar);
                O(new Int32Or());
                OIf(); Ret(2); OEnd();

                TagIs(CA, 0);
                OIf();
                {
                    TagIs(CB, 0);
                    OIf();
                    {
                        // var-var, distinct: the YOUNGER home takes the ref.
                        LG(DA); LG(DB); O(new Int32LessThanSigned());
                        OIf(); Bind(DB, CA);
                        OElse(); Bind(DA, CB);
                        OEnd();
                    }
                    OElse();
                    Bind(DA, CB);
                    OEnd();
                    Continue();
                }
                OEnd();
                TagIs(CB, 0);
                OIf(); Bind(DB, CA); Continue(); OEnd();

                LG(CA); I64(60); O(new Int64ShiftRightUnsigned());
                LG(CB); I64(60); O(new Int64ShiftRightUnsigned());
                O(new Int64NotEqual());
                OIf(); Ret(0); OEnd();

                // Same immediate tag, different cells: a plain mismatch
                // (Int and Atom cells carry their whole identity).
                TagIs(CA, (long)Tag.Int); TagIs(CA, (long)Tag.Atom);
                O(new Int32Or());
                OIf(); Ret(0); OEnd();

                TagIs(CA, (long)Tag.Str);
                OIf();
                {
                    LG(CA); O(new Int32WrapInt64()); LSet(DA);
                    LG(CB); O(new Int32WrapInt64()); LSet(DB);
                    HeapLoad(DA); LSet(FA);
                    HeapLoad(DB); LSet(C1);
                    LG(FA); LG(C1); O(new Int64NotEqual());
                    OIf(); Ret(0); OEnd();
                    LG(FTABB);
                    LG(FA); I64(Cell.PayloadMask); O(new Int64And());
                    O(new Int32WrapInt64()); I32(3); O(new Int32ShiftLeft());
                    O(new Int32Add()); O(new Int64Load());
                    O(new Int32WrapInt64()); LSet(K);
                    OBlock(); OLoop();
                    {
                        // le_s, not eq: the arity comes from the host's mirror,
                        // and a negative one would walk past the exit and only
                        // stop when the worklist filled. Same one instruction.
                        LG(K); I32(0); O(new Int32LessThanOrEqualSigned());
                        O(new BranchIf(1));
                        LG(WL); I32(16); O(new Int32Add()); LG(WLLIM);
                        O(new Int32GreaterThanSigned());
                        OIf(); Ret(2); OEnd();
                        // the K-th args: base + K on both sides
                        LG(WL); LG(DA); LG(K); O(new Int32Add());
                        O(new Int64ExtendInt32Unsigned()); O(new Int64Store());
                        LG(WL); LG(DB); LG(K); O(new Int32Add());
                        O(new Int64ExtendInt32Unsigned());
                        O(new Int64Store { Offset = 8 });
                        LG(WL); I32(16); O(new Int32Add()); LSet(WL);
                        LG(K); I32(1); O(new Int32Subtract()); LSet(K);
                        O(new Branch(0));
                    }
                    OEnd(); OEnd();
                    Continue();
                }
                OEnd();

                TagIs(CA, (long)Tag.Lis);
                OIf();
                {
                    LG(CA); O(new Int32WrapInt64()); LSet(DA);
                    LG(CB); O(new Int32WrapInt64()); LSet(DB);
                    LG(WL); I32(32); O(new Int32Add()); LG(WLLIM);
                    O(new Int32GreaterThanSigned());
                    OIf(); Ret(2); OEnd();
                    PushPairSlot(DA, 0, 0);
                    PushPairSlot(DB, 0, 8);
                    PushPairSlot(DA, 1, 16);
                    PushPairSlot(DB, 1, 24);
                    LG(WL); I32(32); O(new Int32Add()); LSet(WL);
                    Continue();
                }
                OEnd();

                TagIs(CA, (long)Tag.Float);
                OIf();
                {
                    void FloatBits(uint cel, uint outLocal)
                    {
                        LG(cel); I64(56); O(new Int64ShiftRightUnsigned());
                        I64(0xF); O(new Int64And());
                        I64(60); O(new Int64ShiftLeft());
                        LG(cel); O(new Int32WrapInt64()); LSet(DA);
                        HeapLoad(DA); I64(Cell.PayloadMask); O(new Int64And());
                        O(new Int64Or()); LSet(outLocal);
                    }
                    FloatBits(CA, FA);
                    FloatBits(CB, C1);
                    LG(FA); LG(C1); O(new Int64Equal());
                    OIf(); Continue(); OEnd();
                    Ret(0);
                }
                OEnd();

                Ret(2);     // BigInt / Rational / PSTR / Foreign: engine logic
            }
            OEnd();
            I32(0);                      // unreachable fallthrough
            O(new End());
            return code;
        }

    }
}

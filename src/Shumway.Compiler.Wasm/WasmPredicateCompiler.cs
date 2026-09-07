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
        var c = new Compilation(predicate, env, floatLiterals);
        c.Decode();
        c.AssignCursors();
        byte[] bytes = c.Emit();
        if (shared) bytes = WasmSharedMemory.Patch(bytes);
        return new WasmEntry(bytes, predicate.FunctorId, predicate.Arity,
                             c.CursorByAddress, c.RegisterDemand);
    }

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

    private const long RawIntTag = (long)Tag.RawInt << Cell.TagShift;

    private sealed record Instr(int Pc, Opcode Op, int Size)
    {
        public int I0, I1, I2, I3, I4;
    }

    private sealed class Compilation(CompiledPredicate predicate, IWasmCompileEnv env,
                                     IReadOnlyList<double>? floatLiterals)
    {
        private readonly CompiledPredicate _p = predicate;
        private readonly IWasmCompileEnv _env = env;
        private readonly IReadOnlyList<double>? _floats = floatLiterals;
        private readonly List<Instr> _instrs = new();
        private readonly Dictionary<int, int> _byPc = new();
        private readonly SortedSet<int> _leaders = new();
        private readonly Dictionary<int, int> _cursorByAddr = new();
        private readonly Dictionary<int, int> _callee = new();   // call-site pc -> functor

        public IReadOnlyDictionary<int, int> CursorByAddress => _cursorByAddr;
        private int _failCase;      // the internal FAIL cursor (not re-enterable)
        private int _caseCount;

        // ------------------------------------------------------------------
        // Decode + census
        // ------------------------------------------------------------------

        public void Decode()
        {
            byte[] code = _p.Bytecode;
            foreach (var site in _p.CallSites)
                _callee[site.OpcodeOffset] = site.CalleeFunctorId;

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
                    var meta = new Instr(pc, op, 6);
                    _byPc[pc] = _instrs.Count;
                    _instrs.Add(meta);
                    pc += 6;
                    continue;
                }
                var info = OpcodeTable.Get(code[pc]);
                if (!info.IsDefined || info.Size <= 0)
                    throw new WasmCompileException($"undecodable opcode 0x{code[pc]:X2} at {pc}");
                var ins = new Instr(pc, op, info.Size);
                if (info.Size >= 5) ins.I0 = BytecodeIO.ReadInt32(code, pc + 1);
                if (info.Size >= 9) ins.I1 = BytecodeIO.ReadInt32(code, pc + 5);
                if (info.Size >= 13) ins.I2 = BytecodeIO.ReadInt32(code, pc + 9);
                if (info.Size >= 17) ins.I3 = BytecodeIO.ReadInt32(code, pc + 13);
                if (info.Size >= 21) ins.I4 = BytecodeIO.ReadInt32(code, pc + 17);
                _byPc[pc] = _instrs.Count;
                _instrs.Add(ins);
                Census(ins);
                pc += info.Size;
            }
        }

        /// <summary>The translatable set, and the reason when it is not.
        /// Everything else in the 57-opcode universe rejects the predicate:
        /// it stays on the tier it was on.</summary>
        private void Census(Instr ins)
        {
            switch (ins.Op)
            {
                case Opcode.SwitchOnTerm:
                case Opcode.SwitchOnInteger:
                case Opcode.SwitchOnAtom:
                case Opcode.SwitchOnArg:
                case Opcode.SwitchOnIntegerArg:
                case Opcode.SwitchOnAtomArg:
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
                    if (_floats is null)
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
            _leaders.Add(0);
            foreach (var ins in _instrs)
            {
                switch (ins.Op)
                {
                    case Opcode.SwitchOnTerm:
                        _leaders.Add(ins.I0); _leaders.Add(ins.I1);
                        _leaders.Add(ins.I2); _leaders.Add(ins.I3);
                        break;
                    case Opcode.SwitchOnArg:
                        _leaders.Add(ins.I1); _leaders.Add(ins.I2);
                        _leaders.Add(ins.I3); _leaders.Add(ins.I4);
                        break;
                    case Opcode.SwitchOnInteger:
                    case Opcode.SwitchOnAtom:
                    case Opcode.SwitchOnIntegerArg:
                    case Opcode.SwitchOnAtomArg:
                    {
                        int tableId = ins.Op is Opcode.SwitchOnInteger or Opcode.SwitchOnAtom
                            ? ins.I0 : ins.I1;
                        var table = _p.SwitchTables[tableId];
                        foreach (int v in table.Values) _leaders.Add(v);
                        _leaders.Add(table.DefaultAddress);
                        break;
                    }
                    case Opcode.Try:
                        _leaders.Add(ins.I0); _leaders.Add(ins.Pc + 9);
                        break;
                    case Opcode.Retry:
                        _leaders.Add(ins.I0); _leaders.Add(ins.Pc + 5);
                        break;
                    case Opcode.Trust:
                        _leaders.Add(ins.I0);
                        break;
                    case Opcode.Call:
                    case Opcode.CallBuiltin:
                        _leaders.Add(ins.Pc + 9);   // the return cursor
                        break;
                    case Opcode.TryMeElse:
                    case Opcode.RetryMeElse:
                        _leaders.Add(ins.I0);       // the else chain
                        break;
                    case Opcode.Jump:
                        _leaders.Add(ins.I0);
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
            _caseCount = next + 1;
        }

        private int CursorOf(int addr) => _cursorByAddr[addr];

        // ------------------------------------------------------------------
        // Emission
        // ------------------------------------------------------------------

        private readonly List<Instruction> _code = new();
        private int _extraDepth;    // If/Block/Loop opened inside the current case
        private int _caseIndex;     // which case body is being emitted

        private void Op(Instruction i) => _code.Add(i);
        private void OpenIf() { Op(new If(BlockType.Empty)); _extraDepth++; }
        private void OpenElse() { Op(new Else()); }
        private void CloseNested() { Op(new End()); _extraDepth--; }
        private void OpenBlock() { Op(new Block(BlockType.Empty)); _extraDepth++; }
        private void OpenLoop() { Op(new Loop(BlockType.Empty)); _extraDepth++; }

        /// <summary>Branch back to the dispatcher loop (LCur must be set).</summary>
        private void BrDispatch()
            => Op(new Branch((uint)(_extraDepth + (_caseCount - 1 - _caseIndex))));

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
            module.Functions.Add(new Function { Type = 0 });
            // Function 1: the general unifier, internal (not exported).
            module.Types.Add(new WebAssemblyType
            {
                Parameters = [WebAssemblyValueType.Int64, WebAssemblyValueType.Int64,
                              WebAssemblyValueType.Int32],
                Returns = [WebAssemblyValueType.Int32],
            });
            module.Functions.Add(new Function { Type = 1 });
            module.Exports.Add(new Export
            {
                Kind = ExternalKind.Function, Index = 0, Name = WasmAbi.EntryExport,
            });

            EmitPrologue();

            // The dispatcher: loop, one block per case, br_table.
            OpenLoop();                                     // never popped via CloseNested
            _extraDepth--;                                  // accounted in BrDispatch instead
            for (int k = _caseCount - 1; k >= 0; k--) Op(new Block(BlockType.Empty));
            Op(new LocalGet(LCur));
            var labels = new uint[_caseCount];
            for (uint k = 0; k < _caseCount; k++) labels[k] = k;
            Op(new BranchTable((uint)_failCase, labels));

            // Case bodies, in cursor order; each preceded by the End that
            // closes its landing block.
            var addrsInOrder = new List<int>(_leaders);
            for (int k = 0; k < _caseCount; k++)
            {
                Op(new End());
                _caseIndex = k;
                if (k == _failCase) EmitFailCase();
                else EmitRun(addrsInOrder[k]);
            }
            Op(new End());                                  // the loop
            EmitReturn(WasmVerdict.Fail);                   // unreachable fallback
            Op(new End());                                  // the function

            module.Codes.Add(new FunctionBody
            {
                Locals =
                [
                    new Local { Count = 16, Type = WebAssemblyValueType.Int32 },
                    new Local { Count = 3, Type = WebAssemblyValueType.Int64 },
                    new Local { Count = 2, Type = WebAssemblyValueType.Int32 },
                    new Local { Count = AEvalMaxDepth, Type = WebAssemblyValueType.Int64 },
                ],
                Code = _code,
            });
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
        }

        private void EmitReturn(WasmVerdict v)
        {
            StoreSlotFromI32Local(WasmAbi.HeapTop, LH);
            StoreSlotFromI32Local(WasmAbi.TrailTop, LTR);
            StoreSlotFromI32Local(WasmAbi.EnvTop, LE);
            StoreSlotFromI32Local(WasmAbi.ChoiceTop, LB);
            StoreSlotFromI32Local(WasmAbi.HeapBacktrack, LHB);
            StoreSlotFromI32Local(WasmAbi.StackTop, LST);
            StoreSlotFromI32Local(WasmAbi.ContinuationPc, LCP);
            StoreSlotFromI32Local(WasmAbi.WriteMode, LMode);
            StoreSlotFromI32Local(WasmAbi.UnifyPointer, LS);
            Op(new Int32Constant((int)v));
            Op(new Return());
        }

        private void EmitDeopt(int bytecodePc)
        {
            StoreSlot64(WasmAbi.Pc, () => Op(new Int64Constant(_env.EncodeDeoptPc(bytecodePc))));
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
        public int RegisterDemand => System.Math.Max(_maxRegister + 1, _p.Arity);

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
                {
                    CellStoreDyn(LHeapB, LDa, 0, () => Op(new Int64Constant(constCell)));
                    Op(new LocalGet(LDa));
                    Op(new LocalGet(LHB));
                    Op(new Int32LessThanSigned());
                    OpenIf();
                    EmitTrailDa(pcForDeopt);
                    CloseNested();
                }
                OpenElse();
                {
                    TagOfC0();
                    Op(new Int32Constant((int)Tag.AttVar));
                    Op(new Int32Equal());
                    OpenIf();
                    EmitDeopt(pcForDeopt);
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
            EmitDeopt(pcForDeopt);
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
        /// module's own encodings -- anything else is foreign.</summary>
        private void EmitFailCase()
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
            Op(new Int64Load { Offset = 4 * 8 });           // ctl[3] = BP (1+arity handled: base+arity*8, +1 cell +3 cells)
            Op(new Int32WrapInt64());
            Op(new LocalSet(LT1));                          // bp

            var bpCursors = new SortedSet<int>();
            foreach (var ins in _instrs)
                switch (ins.Op)
                {
                    case Opcode.Try: bpCursors.Add(CursorOf(ins.Pc + 9)); break;
                    case Opcode.Retry: bpCursors.Add(CursorOf(ins.Pc + 5)); break;
                    case Opcode.TryMeElse:
                    case Opcode.RetryMeElse: bpCursors.Add(CursorOf(ins.I0)); break;
                }
            foreach (int cursor in bpCursors)
            {
                Op(new LocalGet(LT1));
                Op(new Int32Constant(_env.EncodeBp(cursor)));
                Op(new Int32Equal());
                OpenIf();
                Op(new Int32Constant(cursor));
                Op(new LocalSet(LCur));
                BrDispatch();
                CloseNested();
            }
            EmitReturn(WasmVerdict.Fail);                   // a foreign CP
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
        private bool EmitInstr(Instr ins)
        {
            switch (ins.Op)
            {
                case Opcode.SwitchOnTerm:
                    EmitSwitchOnTerm(ins.Pc, 0, ins.I0, ins.I1, ins.I2, ins.I3);
                    return true;
                case Opcode.SwitchOnArg:
                    EmitSwitchOnTerm(ins.Pc, ins.I0, ins.I1, ins.I2, ins.I3, ins.I4);
                    return true;
                case Opcode.SwitchOnInteger: EmitSwitchOnInteger(0, ins.I0); return true;
                case Opcode.SwitchOnIntegerArg: EmitSwitchOnInteger(ins.I0, ins.I1); return true;
                case Opcode.SwitchOnAtom: EmitSwitchOnAtom(0, ins.I0); return true;
                case Opcode.SwitchOnAtomArg: EmitSwitchOnAtom(ins.I0, ins.I1); return true;
                case Opcode.Try: EmitTry(ins); return true;
                case Opcode.Retry: EmitRetry(ins); return true;
                case Opcode.Trust: EmitTrust(ins); return true;
                case Opcode.Allocate: EmitAllocate(ins); return false;
                case Opcode.Deallocate: EmitDeallocate(); return false;
                case Opcode.DeallocateProceed:
                    EmitFlagsCheck(ins.Pc);
                    EmitDeallocate();
                    EmitReturn(WasmVerdict.Success);
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
                    RegStore(ins.I1, () => Op(new Int64Constant(Cell.Atom(ins.I0).Data)));
                    return false;
                case Opcode.PutNil:
                    RegStore(ins.I0, () => Op(new Int64Constant(Cell.Atom(AtomTable.EmptyListId).Data)));
                    return false;
                case Opcode.GetInteger:
                    RegLoad(ins.I1); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(Cell.Int(ins.I0).Data, ins.Pc);
                    return false;
                case Opcode.GetAtom:
                    RegLoad(ins.I1); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(Cell.Atom(ins.I0).Data, ins.Pc);
                    return false;
                case Opcode.GetNil:
                    RegLoad(ins.I0); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(Cell.Atom(AtomTable.EmptyListId).Data, ins.Pc);
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
                    EmitUnifyConst(Cell.Atom(ins.I0).Data, ins.Pc); return false;
                case Opcode.UnifyInteger:
                    EmitUnifyConst(Cell.Int(ins.I0).Data, ins.Pc); return false;
                case Opcode.UnifyNil:
                    EmitUnifyConst(Cell.Atom(AtomTable.EmptyListId).Data, ins.Pc); return false;
                case Opcode.UnifyVoid: EmitUnifyVoid(ins.I0, ins.Pc); return false;
                case Opcode.UnifyStructure: EmitUnifyStructure(ins.I0, ins.Pc); return false;
                case Opcode.UnifyList: EmitUnifyList(ins.Pc); return false;
                case Opcode.GetConstantA1:
                    RegLoad(0); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(Cell.Atom(ins.I0).Data, ins.Pc); return false;
                case Opcode.GetConstantA2:
                    RegLoad(1); Op(new LocalSet(LC0)); Deref();
                    UnifyC0WithConst(Cell.Atom(ins.I0).Data, ins.Pc); return false;
                case Opcode.PutConstantA1:
                    RegStore(0, () => Op(new Int64Constant(Cell.Atom(ins.I0).Data)));
                    return false;
                case Opcode.PutConstantA2:
                    RegStore(1, () => Op(new Int64Constant(Cell.Atom(ins.I0).Data)));
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
                    EmitReturn(WasmVerdict.Success);
                    return true;
                case Opcode.CutDeallocateProceed:
                    EmitFlagsCheck(ins.Pc);
                    EmitCut(() => { YLoad(ins.I0); Op(new Int32WrapInt64()); });
                    EmitDeallocate();
                    EmitReturn(WasmVerdict.Success);
                    return true;
                case Opcode.CallBuiltin:
                    // The id is the operand itself: the module compiler
                    // resolves builtins when the registry is loaded, exactly
                    // as the linker would. The env-trim count (I1) rides the
                    // high half of the id slot; -1 is the no-trim sentinel.
                    EmitFlagsCheck(ins.Pc);
                    if (!_env.IsDirectBuiltin(ins.I0)) { EmitDeopt(ins.Pc); return true; }
                    StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(
                        (uint)ins.I0 | ((long)ins.I1 << 32))));
                    StoreSlot64(WasmAbi.Cursor,
                        () => Op(new Int64Constant(CursorOf(ins.Pc + 9))));
                    EmitReturn(WasmVerdict.BuiltinRequest);
                    return true;
                case Opcode.ExecuteBuiltin:
                    EmitFlagsCheck(ins.Pc);
                    if (!_env.IsDirectBuiltin(ins.I0)) { EmitDeopt(ins.Pc); return true; }
                    StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant((uint)ins.I0)));
                    StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                    EmitReturn(WasmVerdict.BuiltinRequest);
                    return true;
                case Opcode.GetFloat: EmitGetFloat(ins.I0, ins.I1, ins.Pc); return false;
                case Opcode.PutFloat: EmitPutFloat(ins.I0, ins.I1, ins.Pc); return false;
                case Opcode.TryMeElse: EmitTryMeElse(ins); return false;
                case Opcode.RetryMeElse: EmitRetryMeElse(ins); return false;
                case Opcode.TrustMe: EmitTrustMe(ins); return false;
                case Opcode.Jump: GoTo(ins.I0); return true;
                case Opcode.AIntBin: EmitAIntBin(ins); return false;
                case Opcode.AEvalPush: EmitAEvalPush(ins); return false;
                case Opcode.AEvalBin: EmitAEvalBin(ins); return false;
                case Opcode.AEvalUn: EmitAEvalUn(ins); return false;
                case Opcode.AEvalIs: EmitAEvalIs(ins); return false;
                case Opcode.AEvalCmp: EmitAEvalCmp(ins); return false;
                case Opcode.Call: EmitCall(ins); return true;
                case Opcode.Execute: EmitExecute(ins); return true;
                default:
                    throw new WasmCompileException($"emit: {ins.Op} at {ins.Pc}");
            }
        }

        // ---- control ----

        private void EmitFlagsCheck(int pc)
        {
            LoadSlot64(WasmAbi.Flags);
            Op(new Int64Constant(0));
            Op(new Int64NotEqual());
            OpenIf();
            EmitDeopt(pc);
            CloseNested();
        }

        private void EmitProceed(Instr ins)
        {
            EmitFlagsCheck(ins.Pc);
            EmitReturn(WasmVerdict.Success);
        }

        private void EmitCall(Instr ins)
        {
            if (!_callee.TryGetValue(ins.Pc, out int callee))
                throw new WasmCompileException($"call at {ins.Pc} has no call site");
            if (_env.TryGetBuiltin(callee, out int builtinId))
            {
                // The builtin runs on the host: leave its id and the return
                // cursor in the mailbox and step out (env trimming skipped;
                // a CP the builtin pushes just sits a little higher).
                EmitFlagsCheck(ins.Pc);
                StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(builtinId)));
                StoreSlot64(WasmAbi.Cursor,
                    () => Op(new Int64Constant(CursorOf(ins.Pc + 9))));
                EmitReturn(WasmVerdict.BuiltinRequest);
                return;
            }
            EmitFlagsCheck(ins.Pc);
            // Env trimming is skipped: frames sit a little higher, results
            // are unaffected. CP becomes the marker that re-enters us at the
            // return cursor. The callee enters a new procedure: refresh its
            // cut barrier (the interpreter's SetB0(B) before every call).
            StoreSlotFromI32Local(WasmAbi.CutBarrier, LB);
            Op(new Int32Constant(_env.EncodeReturnMarker(CursorOf(ins.Pc + 9))));
            Op(new LocalSet(LCP));
            StoreSlot64(WasmAbi.Pc,
                () => Op(new Int64Constant(_env.EncodeCallTarget(callee))));
            EmitReturn(WasmVerdict.SuccessTailCall);
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
                // A builtin in tail position: run it, then proceed. Cursor -1
                // is that convention on the wire.
                StoreSlot64(WasmAbi.BuiltinId, () => Op(new Int64Constant(builtinId)));
                StoreSlot64(WasmAbi.Cursor, () => Op(new Int64Constant(-1)));
                EmitReturn(WasmVerdict.BuiltinRequest);
                return;
            }
            if (callee == _p.FunctorId)
            {
                // The self tail call: back to the entry dispatch, unless the
                // heap has crossed the watermark (the engine collects there).
                Op(new LocalGet(LH));
                LoadSlot32(WasmAbi.HeapWatermark);
                Op(new Int32GreaterThanOrEqualSigned());
                OpenIf();
                EmitDeopt(pc);
                CloseNested();
                // A tail call still enters a new procedure: the next
                // iteration's neck_cut must see B as of THIS dispatch, not
                // the barrier the original entry came in with -- a body that
                // left choice points would be over-cut (SetB0(B) parity).
                StoreSlotFromI32Local(WasmAbi.CutBarrier, LB);
                Op(new Int32Constant(0));
                Op(new LocalSet(LCur));
                BrDispatch();
                return;
            }
            StoreSlotFromI32Local(WasmAbi.CutBarrier, LB);
            StoreSlot64(WasmAbi.Pc,
                () => Op(new Int64Constant(_env.EncodeCallTarget(callee))));
            EmitReturn(WasmVerdict.SuccessTailCall);
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
            EmitDeopt(pc);
            CloseNested();
            GoTo(varA);
        }

        private void EmitSwitchOnInteger(int reg, int tableId)
        {
            var table = _p.SwitchTables[tableId];
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            TagOfC0();
            Op(new Int32Constant((int)Tag.Int));
            Op(new Int32NotEqual());
            OpenIf();
            GoTo(table.DefaultAddress);
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
                GoTo(table.Values[k]);
                CloseNested();
            }
            GoTo(table.DefaultAddress);
        }

        private void EmitSwitchOnAtom(int reg, int tableId)
        {
            var table = _p.SwitchTables[tableId];
            RegLoad(reg); Op(new LocalSet(LC0)); Deref();
            for (int k = 0; k < table.Count; k++)
            {
                Op(new LocalGet(LC0));
                Op(new Int64Constant(Cell.Atom(table.Keys[k]).Data));
                Op(new Int64Equal());
                OpenIf();
                GoTo(table.Values[k]);
                CloseNested();
            }
            GoTo(table.DefaultAddress);
        }

        // ---- choice points (the engine's own layout, cell for cell) ----

        private void EmitTry(Instr ins)
            => EmitPushChoicePoint(ins.Pc, ins.I1, CursorOf(ins.Pc + 9), gotoAddr: ins.I0);

        /// <summary>The engine's PushChoicePoint, cell for cell; jumps to
        /// <paramref name="gotoAddr"/> afterwards when one is given, else
        /// falls through.</summary>
        private void EmitPushChoicePoint(int pc, int arity, int bpCursor, int gotoAddr = -1)
        {
            int size = 11 + arity;
            Op(new LocalGet(LST));
            Op(new Int32Constant(size));
            Op(new Int32Add());
            LoadSlot32(WasmAbi.StackLimit);
            Op(new Int32GreaterThanSigned());
            OpenIf();
            EmitDeopt(pc);
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
                Op(new Int32Constant(_env.EncodeBp(bpCursor)))));
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
            // regs[t2] = stack[B + 1 + t2]
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

            // ctlBase (a byte address) = &stack[B] + (1 + arity) * 8
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
            EmitDeopt(pcForDeopt);
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
            RawInt(() => Op(new Int32Constant(_env.EncodeBp(CursorOf(ins.Pc + 5)))));
            Op(new Int64Store { Offset = 4 * 8 });                  // ctl[3]
            GoTo(ins.I0);
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
            GoTo(ins.I0);
        }

        // ---- frames ----

        private void EmitAllocate(Instr ins)
        {
            int n = ins.I0;
            int size = 3 + n;
            Op(new LocalGet(LST));
            Op(new Int32Constant(size));
            Op(new Int32Add());
            LoadSlot32(WasmAbi.StackLimit);
            Op(new Int32GreaterThanSigned());
            OpenIf();
            EmitDeopt(ins.Pc);
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

        private void EmitUnifyTwo(Action loadLeft, Action loadRight, int pc)
        {
            // Unifies two cells (get_value_y / get_value_x). Both sides
            // derefed; the general shapes step aside.
            var ins = new Instr(pc, Opcode.GetValueY, 0);   // pc carrier for the emits below
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
                        CellStoreDyn(LHeapB, LDa, 0, () =>
                        { Op(new LocalGet(LT1)); Op(new Int64ExtendInt32Unsigned()); });
                        Op(new LocalGet(LDa)); Op(new LocalGet(LHB));
                        Op(new Int32LessThanSigned());
                        OpenIf();
                        EmitTrailDa(ins.Pc);
                        CloseNested();
                    }
                    OpenElse();
                    {
                        CellStoreDyn(LHeapB, LDa, 0, () => Op(new LocalGet(LC2)));
                        Op(new LocalGet(LDa)); Op(new LocalGet(LHB));
                        Op(new Int32LessThanSigned());
                        OpenIf();
                        EmitTrailDa(ins.Pc);
                        CloseNested();
                    }
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
                        CellStoreDyn(LHeapB, LDa, 0, () => Op(new LocalGet(LC0)));
                        Op(new LocalGet(LDa)); Op(new LocalGet(LHB));
                        Op(new Int32LessThanSigned());
                        OpenIf();
                        EmitTrailDa(ins.Pc);
                        CloseNested();
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
                        Op(new WebAssembly.Instructions.Call(1));
                        Op(new LocalSet(LT0));
                        LoadSlot32(WasmAbi.TrailTop); Op(new LocalSet(LTR));
                        Op(new LocalGet(LT0)); Op(new Int32Constant(0)); Op(new Int32Equal());
                        OpenIf();
                        GoFail();
                        CloseNested();
                        Op(new LocalGet(LT0)); Op(new Int32Constant(2)); Op(new Int32Equal());
                        OpenIf();
                        EmitDeopt(ins.Pc);
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
            EmitDeopt(pc);
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
            EmitDeopt(pc);
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
            EmitReadIntOperand(packed & 0xFF, ins.I1, ins.Pc);
            Op(new LocalGet(LC1)); Op(new LocalSet(LC2));
            EmitReadIntOperand((packed >> 8) & 0xFF, ins.I2, ins.Pc);
            Op(new LocalGet(LC2));
            Op(new LocalGet(LC1));
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

        private void EmitAIntBin(Instr ins)
        {
            int packed = ins.I0;
            EmitReadIntOperand(packed & 0xFF, ins.I1, ins.Pc);
            Op(new LocalGet(LC1)); Op(new LocalSet(LC2));           // a
            EmitReadIntOperand((packed >> 8) & 0xFF, ins.I2, ins.Pc);   // b in LC1
            EmitIntBinCore((packed >> 24) & 0xFF, ins.Pc);
            EmitBoxC0IntoC2();
            EmitDeliverInt((packed >> 16) & 0xFF, ins.I3, ins.Pc);
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
                EmitDeopt(pc);
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
                        EmitDeopt(pcDeopt);
                        CloseNested();
                    }
                    CloseNested();
                    FitsCheckC0(pcDeopt);
                    break;
                case 4:     // IntDiv (truncating)
                    Op(new LocalGet(LC1)); Op(new Int64Constant(0)); Op(new Int64Equal());
                    OpenIf();
                    EmitDeopt(pcDeopt);
                    CloseNested();
                    Op(new LocalGet(LC2)); Op(new LocalGet(LC1)); Op(new Int64DivideSigned());
                    Op(new LocalSet(LC0));
                    break;
                case 5:     // Mod (sign of the divisor)
                    Op(new LocalGet(LC1)); Op(new Int64Constant(0)); Op(new Int64Equal());
                    OpenIf();
                    EmitDeopt(pcDeopt);
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
                    EmitDeopt(pcDeopt);
                    Op(new Int64Constant(0)); Op(new LocalSet(LC0));    // unreachable
                    break;
            }
        }

        /// <summary>Boxes the plain i64 in LC0 as an Int cell into LC2.</summary>
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
                        {
                            CellStoreDyn(LHeapB, LDa, 0, () => Op(new LocalGet(LC2)));
                            Op(new LocalGet(LDa)); Op(new LocalGet(LHB));
                            Op(new Int32LessThanSigned());
                            OpenIf();
                            EmitTrailDa(pcDeopt);
                            CloseNested();
                        }
                        OpenElse();
                        {
                            TagOfC0(); Op(new Int32Constant((int)Tag.Int)); Op(new Int32Equal());
                            OpenIf();
                            GoFail();
                            CloseNested();
                            EmitDeopt(pcDeopt);
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
            EmitDeopt(pc);
            CloseNested();
        }

        /// <summary>heap[LDa] = value, trailed when old (the engine's Bind).</summary>
        private void EmitBindDa(int pc, Action value)
        {
            CellStoreDyn(LHeapB, LDa, 0, value);
            Op(new LocalGet(LDa));
            Op(new LocalGet(LHB));
            Op(new Int32LessThanSigned());
            OpenIf();
            EmitTrailDa(pc);
            CloseNested();
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
            long functorCell = Cell.Functor(functorId).Data;
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
                    EmitDeopt(pc);
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
                    EmitDeopt(pc);
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
                () => Op(new Int64Constant(Cell.Functor(functorId).Data)));
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
            long functorCell = Cell.Functor(functorId).Data;
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
                        EmitDeopt(pc);
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
                        EmitDeopt(pc);
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
            EmitPushChoicePoint(ins.Pc, arity, CursorOf(ins.I0));
            // ...and falls through into the first alternative.
        }

        private void EmitRetryMeElse(Instr ins)
        {
            EmitRestoreCommon(ins.Pc);
            Op(new LocalGet(LH)); Op(new LocalSet(LHB));            // AssignHb(H)
            Op(new LocalGet(LT1));
            RawInt(() => Op(new Int32Constant(_env.EncodeBp(CursorOf(ins.I0)))));
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

        private void EmitGetFloat(int literalId, int reg, int pc)
        {
            double value = _floats![literalId];
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
                    EmitDeopt(pc);
                    CloseNested();
                    GoFail();
                }
                CloseNested();
            }
            CloseNested();
        }

        private void EmitPutFloat(int literalId, int reg, int pc)
        {
            // The register takes a REF to the header, as the engine's
            // put_float does.
            EmitHeapGuard(pc, 2);
            EmitWriteFloatAtH(_floats![literalId]);
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
                    break;
                case 3:
                case 4:
                    EmitReadIntOperand(ins.I0, ins.I1, _aevalStart);
                    Op(new LocalGet(LC1));
                    Op(new LocalSet(LA(_aevalDepth)));
                    break;
                default:
                    // bigint / float literal: the sequence always escalates.
                    EmitDeopt(_aevalStart);
                    Op(new Int64Constant(0));                       // unreachable
                    Op(new LocalSet(LA(_aevalDepth)));
                    break;
            }
            _aevalDepth++;
        }

        private void EmitAEvalBin(Instr ins)
        {
            if (_aevalDepth < 2)
                throw new WasmCompileException($"a_eval_bin underflow at {ins.Pc}");
            Op(new LocalGet(LA(_aevalDepth - 2))); Op(new LocalSet(LC2));
            Op(new LocalGet(LA(_aevalDepth - 1))); Op(new LocalSet(LC1));
            EmitIntBinCore(ins.I0, _aevalStart);
            Op(new LocalGet(LC0));
            Op(new LocalSet(LA(_aevalDepth - 2)));
            _aevalDepth--;
        }

        private void EmitAEvalUn(Instr ins)
        {
            if (_aevalDepth < 1)
                throw new WasmCompileException($"a_eval_un underflow at {ins.Pc}");
            uint a = LA(_aevalDepth - 1);

            void FitsCheck()
            {
                Op(new LocalGet(a)); Op(new Int64Constant(Cell.MinInt60));
                Op(new Int64LessThanSigned());
                Op(new LocalGet(a)); Op(new Int64Constant(Cell.MaxInt60));
                Op(new Int64GreaterThanSigned());
                Op(new Int32Or());
                OpenIf();
                EmitDeopt(_aevalStart);
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
                    EmitDeopt(_aevalStart);
                    break;
            }
        }

        private void EmitAEvalIs(Instr ins)
        {
            if (_aevalDepth != 1)
                throw new WasmCompileException($"a_eval_is at depth {_aevalDepth} at {ins.Pc}");
            Op(new LocalGet(LA(0))); Op(new LocalSet(LC0));
            EmitBoxC0IntoC2();
            EmitDeliverInt(ins.I0, ins.I1, _aevalStart);
            _aevalDepth = 0;
        }

        private void EmitAEvalCmp(Instr ins)
        {
            if (_aevalDepth != 2)
                throw new WasmCompileException($"a_eval_cmp at depth {_aevalDepth} at {ins.Pc}");
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
                StoreConst(0, Cell.Functor(fid).Data);
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
                        StoreConst(writePos, Cell.Atom(ins.I0).Data); done = Advance(); break;
                    case Opcode.UnifyInteger:
                        StoreConst(writePos, Cell.Int(ins.I0).Data); done = Advance(); break;
                    case Opcode.UnifyNil:
                        StoreConst(writePos, Cell.Atom(AtomTable.EmptyListId).Data);
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
                        StoreConst(nested, Cell.Functor(ins.I0).Data);
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
        // from the host-mirrored table at FunctorArityBase, because the
        // functor table is managed state. Attvars, bigints, rationals and
        // PSTRs deopt: their unification is engine logic. A deopt after
        // partial binding is sound -- everything bound so far was required,
        // is trailed, and re-unifies idempotently when the interpreter
        // re-runs the instruction.

        private static List<Instruction> BuildUnifierBody()
        {
            const uint PA = 0, PB = 1, MB = 2;
            const uint HEAPB = 3, TRAILB = 4, ARITYB = 5, TR = 6, HHB = 7,
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
            SlotToI32(WasmAbi.FunctorArityBase, ARITYB);
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
                    LG(ARITYB);
                    LG(FA); I64(Cell.PayloadMask); O(new Int64And());
                    O(new Int32WrapInt64()); I32(2); O(new Int32ShiftLeft());
                    O(new Int32Add()); O(new Int32Load()); LSet(K);
                    OBlock(); OLoop();
                    {
                        LG(K); I32(0); O(new Int32Equal()); O(new BranchIf(1));
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

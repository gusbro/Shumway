using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Interpreter;

public class CompoundOpcodeTests
{
    // ---------- Helpers ----------

    private static byte[] BuildCode(params object[] tokens)
    {
        int size = 0;
        foreach (var t in tokens)
        {
            if (t is Opcode) size++;
            else if (t is int) size += 4;
            else throw new ArgumentException($"Unexpected token type {t?.GetType()}");
        }
        var code = new byte[size];
        int p = 0;
        foreach (var t in tokens)
        {
            if (t is Opcode op) code[p++] = (byte)op;
            else { BytecodeIO.WriteInt32(code, p, (int)t!); p += 4; }
        }
        return code;
    }

    // ---------- put_structure ----------

    [Fact]
    public void PutStructure_BuildsStrAndFunctorAndEntersWriteMode()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 2);

        var code = BuildCode(Opcode.PutStructure, fooFunctor, 0, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        // ADR-017 phase 2: the STR tag rides inline in the register, pointing
        // straight at the FUNCTOR cell (a structure is functor + n args, with no
        // separate on-heap STR header).
        Cell x0 = engine.GetRegister(0);
        Assert.Equal(Tag.Str, x0.Tag);

        int functorIdx = x0.AsHeapIndex;
        Cell functorCell = engine.GetHeap(functorIdx);
        Assert.Equal(Tag.Functor, functorCell.Tag);
        Assert.Equal(fooFunctor, functorCell.AsFunctorId);

        Assert.True(engine.WriteMode);
        Assert.Equal(functorIdx + 1, engine.UnifyPointer);
    }

    [Fact]
    public void PutStructure_FollowedByUnifyConstants_BuildsCompleteCompound()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 2);

        // put_structure foo/2, X[0]; unify_constant 100; unify_constant 101; halt
        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyConstant, 100,
            Opcode.UnifyConstant, 101,
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        // ADR-017: x0 is the inline STR pointing at the FUNCTOR cell; the two
        // args sit immediately after it.
        Cell x0 = engine.GetRegister(0);
        int functorIdx = x0.AsHeapIndex;
        Assert.Equal(Cell.Atom(100), engine.GetHeap(functorIdx + 1));
        Assert.Equal(Cell.Atom(101), engine.GetHeap(functorIdx + 2));
    }

    // ---------- get_structure ----------

    [Fact]
    public void GetStructure_OnMatchingCompound_EntersReadMode()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 2);

        // Build foo(100, 101) first, then re-open it via get_structure.
        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyConstant, 100,
            Opcode.UnifyConstant, 101,
            Opcode.GetStructure, fooFunctor, 0,    // open in read mode
            Opcode.UnifyConstant, 100,              // match arg 1
            Opcode.UnifyConstant, 101,              // match arg 2
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.False(engine.WriteMode);             // ended in read mode after get_structure
    }

    [Fact]
    public void GetStructure_FunctorMismatch_Fails()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 2);
        int barFunctor = FunctorTable.Intern(atomId: 201, arity: 2);

        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyConstant, 100,
            Opcode.UnifyConstant, 101,
            Opcode.GetStructure, barFunctor, 0,     // mismatched functor — fails
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    [Fact]
    public void GetStructure_OnUnboundVariable_EntersWriteMode()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 1);
        int heapIdx = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(heapIdx));

        var code = BuildCode(Opcode.GetStructure, fooFunctor, 0, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Assert.True(engine.WriteMode);              // unbound → write mode
        // ADR-017: the unbound var is bound directly to an inline STR cell that
        // points at the freshly allocated FUNCTOR cell — no on-heap STR header.
        Cell bound = engine.GetHeap(heapIdx);
        Assert.Equal(Tag.Str, bound.Tag);
        Cell functorCell = engine.GetHeap(bound.AsHeapIndex);
        Assert.Equal(Tag.Functor, functorCell.Tag);
    }

    // ---------- put_list and get_list ----------

    [Fact]
    public void PutList_BuildsLisAndEntersWriteMode()
    {
        var engine = new Engine();
        var code = BuildCode(Opcode.PutList, 0, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        // ADR-017: the LIS tag rides inline in the register, pointing at the
        // 2-cell [head, tail] pair the following unify_* opcodes will write.
        // put_list itself writes no heap cell; it just positions the pointer.
        Cell x0 = engine.GetRegister(0);
        Assert.Equal(Tag.Lis, x0.Tag);

        int pair = x0.AsHeapIndex;
        Assert.True(engine.WriteMode);
        Assert.Equal(pair, engine.UnifyPointer);
    }

    [Fact]
    public void GetList_OnExistingList_EntersReadMode()
    {
        var engine = new Engine();

        // Build [100] (single-cons list) first.
        var build = BuildCode(
            Opcode.PutList, 0,
            Opcode.UnifyConstant, 100,
            Opcode.UnifyNil,
            Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(build, 0));

        // Re-open it with get_list and verify read mode.
        var get = BuildCode(
            Opcode.GetList, 0,
            Opcode.UnifyConstant, 100,    // match head = 100
            Opcode.UnifyNil,               // match tail = []
            Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(get, 0));
        Assert.False(engine.WriteMode);
    }

    [Fact]
    public void GetList_OnAtom_Fails()
    {
        var engine = new Engine();
        engine.SetRegister(0, Cell.Atom(100));      // X[0] is an atom, not a list

        var code = BuildCode(Opcode.GetList, 0, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    [Fact]
    public void GetListA1_DispatchesToX0()
    {
        var engine = new Engine();
        var build = BuildCode(
            Opcode.PutList, 0,
            Opcode.UnifyConstant, 42,
            Opcode.UnifyNil,
            Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(build, 0));

        var get = BuildCode(Opcode.GetListA1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(get, 0));
        Assert.False(engine.WriteMode);
    }

    // ---------- unify_variable in both modes ----------

    [Fact]
    public void UnifyVariableX_WriteMode_AllocatesFreshUnbound()
    {
        // Build foo/1 with an unbound arg captured into X[1].
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 1);
        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyVariableX, 1,       // X[1] := fresh heap unbound
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Cell x1 = engine.GetRegister(1);
        Assert.Equal(Tag.Ref, x1.Tag);
        int target = x1.AsHeapIndex;
        Assert.Equal(Cell.UnboundVar(target), engine.GetHeap(target));
    }

    [Fact]
    public void UnifyVariableX_ReadMode_CopiesArgIntoRegister()
    {
        // Build foo(100) and then read it back, capturing the arg into X[1].
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 1);

        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyConstant, 100,
            Opcode.GetStructure, fooFunctor, 0,
            Opcode.UnifyVariableX, 1,        // X[1] := heap[unifyPointer]
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(100), engine.GetRegister(1));
    }

    // ---------- unify_value in both modes ----------

    [Fact]
    public void UnifyValueX_WriteMode_CopiesRegisterToHeap()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 1);
        engine.SetRegister(1, Cell.Atom(99));

        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyValueX, 1,          // heap[unifyPointer] := X[1] = Atom(99)
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        // ADR-017: register holds the inline STR pointing at the FUNCTOR cell;
        // the single arg sits immediately after it.
        int functorIdx = engine.GetRegister(0).AsHeapIndex;
        Assert.Equal(Cell.Atom(99), engine.GetHeap(functorIdx + 1));
    }

    [Fact]
    public void UnifyValueX_ReadMode_UnifiesAgainstHeap_Success()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 1);
        engine.SetRegister(1, Cell.Atom(100));

        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyConstant, 100,
            Opcode.GetStructure, fooFunctor, 0,
            Opcode.UnifyValueX, 1,           // Unify X[1] = Atom(100) with heap arg = Atom(100)
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void UnifyValueX_ReadMode_Mismatch_Fails()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 1);
        engine.SetRegister(1, Cell.Atom(999));    // X[1] differs from heap arg (100)

        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyConstant, 100,
            Opcode.GetStructure, fooFunctor, 0,
            Opcode.UnifyValueX, 1,
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    // ---------- unify_void ----------

    [Fact]
    public void UnifyVoid_WriteMode_AllocatesAnonymousUnbounds()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 3);
        int heapBefore = engine.HeapTop;

        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyVoid, 3,
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        // ADR-017: put_structure allocates 1 cell (FUNCTOR; the STR tag rides
        // inline in the register), unify_void 3 allocates 3 more = 4 total.
        Assert.Equal(heapBefore + 4, engine.HeapTop);
    }

    [Fact]
    public void UnifyVoid_ReadMode_AdvancesPointerWithoutAllocating()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 2);

        // Build foo(100, 101), then read back skipping both args via unify_void 2.
        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,
            Opcode.UnifyConstant, 100,
            Opcode.UnifyConstant, 101,
            Opcode.GetStructure, fooFunctor, 0,
            Opcode.UnifyVoid, 2,
            Opcode.Halt);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.False(engine.WriteMode);
    }

    // ---------- unify_constant failure with backtrack ----------

    [Fact]
    public void UnifyConstant_ReadMode_BacktracksOnMismatch()
    {
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 1);

        // Build foo(100). Then try_me_else creates a CP, and we attempt to match against
        // foo(999); that fails inside unify_constant which must redirect to BP.
        //
        // Layout:
        //   0..8:   put_structure foo/1, X[0]
        //   9..13:  unify_constant 100
        //   14..22: try_me_else BP=33, arity=0
        //   23..31: get_structure foo/1, X[0]
        //   32..36: unify_constant 999          ; FAILS — heap arg is 100
        //   37:     halt                         ; success branch (never taken)
        //   38:     halt                         ; backtrack target (BP) — placed at 33
        //
        // Adjust: BP must point to a halt. Easier — recompute.
        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,    // 0..8
            Opcode.UnifyConstant, 100,              // 9..13
            Opcode.TryMeElse, 38, 0,                // 14..22 (BP=38)
            Opcode.GetStructure, fooFunctor, 0,    // 23..31
            Opcode.UnifyConstant, 999,              // 32..36 — fails
            Opcode.Halt,                            // 37 (success branch)
            Opcode.Halt);                           // 38 (BP target)

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(38, engine.P);                 // landed on the backtrack target
    }

    // ---------- Integration ----------

    [Fact]
    public void Integration_BuildFooAB_ThenHeadMatchCapturesArgs()
    {
        // Caller builds foo(a=100, b=101) in X[0], then calls a clause "p(foo(X, Y))"
        // which captures X, Y via unify_variable_x. After the call, X[1] = a and X[2] = b.
        //
        // Bytecode:
        //   0..8:   put_structure foo/2, X[0]
        //   9..13:  unify_constant 100
        //   14..18: unify_constant 101
        //   19..27: call p=33, 0
        //   28:     halt
        //   ... pad to 33
        //   33..41: get_structure foo/2, X[0]
        //   42..46: unify_variable_x 1
        //   47..51: unify_variable_x 2
        //   52:     proceed
        var engine = new Engine();
        int fooFunctor = FunctorTable.Intern(atomId: 200, arity: 2);

        var code = BuildCode(
            Opcode.PutStructure, fooFunctor, 0,    // 0..8
            Opcode.UnifyConstant, 100,              // 9..13
            Opcode.UnifyConstant, 101,              // 14..18
            Opcode.Call, 28, 0,                     // 19..27 — call p at 28
            Opcode.Halt,                            // 28
            // wait — call target should land on get_structure. Let me redo with correct offsets.
            // Easier: pad the call target to 29 and halt at 28.
            Opcode.GetStructure, fooFunctor, 0,    // 29..37
            Opcode.UnifyVariableX, 1,               // 38..42
            Opcode.UnifyVariableX, 2,               // 43..47
            Opcode.Proceed);                        // 48

        // Recompute target: call ends at 28 (PC after call = 28 = CP). So target should be 29
        // for the predicate entry.
        // Fix the call target operand:
        BytecodeIO.WriteInt32(code, 20, 29);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Assert.Equal(Cell.Atom(100), engine.GetRegister(1));
        Assert.Equal(Cell.Atom(101), engine.GetRegister(2));
    }
}

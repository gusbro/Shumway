using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Interpreter;

public class GetPutOpcodeTests
{
    // ---------- Helpers ----------

    /// <summary>Builds a bytecode array from a mix of opcodes and 32-bit int operands.
    /// Each Opcode contributes 1 byte; each int contributes 4 bytes (little-endian).</summary>
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

    private static (Engine engine, BytecodeInterpreter interp) NewEngine()
    {
        var engine = new Engine();
        return (engine, new BytecodeInterpreter(engine));
    }

    // ---------- get_variable_x / get_variable_y ----------

    [Fact]
    public void GetVariableX_CopiesRegister()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(1, Cell.Atom(42));

        var code = BuildCode(Opcode.GetVariableX, 0, 1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(42), engine.GetRegister(0));
    }

    [Fact]
    public void GetVariableY_WritesRegisterIntoPermanent()
    {
        var (engine, interp) = NewEngine();
        engine.Allocate(1);
        engine.SetRegister(0, Cell.Atom(42));

        var code = BuildCode(Opcode.GetVariableY, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(42), engine.GetY(0));
    }

    // ---------- get_value_x / get_value_y ----------

    [Fact]
    public void GetValueX_TwoUnboundVars_UnifyTogether()
    {
        var (engine, interp) = NewEngine();
        int x0 = engine.AllocateHeapUnbound();
        int x1 = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(x0));
        engine.SetRegister(1, Cell.Ref(x1));

        var code = BuildCode(Opcode.GetValueX, 0, 1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(engine.Deref(x0), engine.Deref(x1));
    }

    [Fact]
    public void GetValueX_MismatchedAtoms_Fails()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(1));
        engine.SetRegister(1, Cell.Atom(2));

        var code = BuildCode(Opcode.GetValueX, 0, 1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    [Fact]
    public void GetValueY_UnifiesPermanentAndRegister()
    {
        var (engine, interp) = NewEngine();
        engine.Allocate(1);
        engine.SetY(0, Cell.Atom(42));
        engine.SetRegister(0, Cell.Atom(42));

        var code = BuildCode(Opcode.GetValueY, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void GetValueY_MismatchedValues_Fails()
    {
        var (engine, interp) = NewEngine();
        engine.Allocate(1);
        engine.SetY(0, Cell.Atom(42));
        engine.SetRegister(0, Cell.Atom(99));

        var code = BuildCode(Opcode.GetValueY, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    // ---------- get_constant / get_atom ----------

    [Fact]
    public void GetConstant_UnboundRegister_BindsToAtom()
    {
        var (engine, interp) = NewEngine();
        int x0 = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(x0));

        var code = BuildCode(Opcode.GetConstant, 42, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(42), engine.GetHeap(x0));
    }

    [Fact]
    public void GetConstant_MatchingAtom_Succeeds()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(42));

        var code = BuildCode(Opcode.GetConstant, 42, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void GetConstant_MismatchedAtom_Fails()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(99));

        var code = BuildCode(Opcode.GetConstant, 42, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    [Fact]
    public void GetAtom_SemanticsMatchGetConstant()
    {
        // get_atom is the named-for-clarity sibling of get_constant.
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(42));

        var code = BuildCode(Opcode.GetAtom, 42, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    // ---------- get_integer ----------

    [Fact]
    public void GetInteger_UnboundRegister_BindsToInt()
    {
        var (engine, interp) = NewEngine();
        int x0 = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(x0));

        var code = BuildCode(Opcode.GetInteger, -123, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Int(-123), engine.GetHeap(x0));
    }

    [Fact]
    public void GetInteger_MismatchedValue_Fails()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Int(7));

        var code = BuildCode(Opcode.GetInteger, 8, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    // ---------- get_nil ----------

    [Fact]
    public void GetNil_MatchingEmptyList_Succeeds()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(AtomTable.EmptyListId));

        var code = BuildCode(Opcode.GetNil, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void GetNil_NonEmptyAtom_Fails()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(AtomTable.TrueId));

        var code = BuildCode(Opcode.GetNil, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    // ---------- put_variable_x / put_variable_y ----------

    [Fact]
    public void PutVariableX_CreatesFreshUnboundInBothRegisters()
    {
        var (engine, interp) = NewEngine();
        var code = BuildCode(Opcode.PutVariableX, 1, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Cell x0 = engine.GetRegister(0);
        Cell x1 = engine.GetRegister(1);
        Assert.Equal(Tag.Ref, x0.Tag);
        Assert.Equal(Tag.Ref, x1.Tag);
        Assert.Equal(x0.AsHeapIndex, x1.AsHeapIndex);
        // The heap cell should be self-referencing (unbound).
        int target = x0.AsHeapIndex;
        Assert.Equal(Cell.UnboundVar(target), engine.GetHeap(target));
    }

    [Fact]
    public void PutVariableY_CreatesFreshVarInPermanentAndRegister()
    {
        var (engine, interp) = NewEngine();
        engine.Allocate(1);

        var code = BuildCode(Opcode.PutVariableY, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Cell y = engine.GetY(0);
        Cell x = engine.GetRegister(0);
        Assert.Equal(Tag.Ref, y.Tag);
        Assert.Equal(Tag.Ref, x.Tag);
        Assert.Equal(y.AsHeapIndex, x.AsHeapIndex);
    }

    // ---------- put_value_x / put_value_y ----------

    [Fact]
    public void PutValueX_CopiesRegisterToRegister()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(2, Cell.Atom(77));

        var code = BuildCode(Opcode.PutValueX, 2, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(77), engine.GetRegister(0));
    }

    [Fact]
    public void PutValueY_CopiesPermanentToRegister()
    {
        var (engine, interp) = NewEngine();
        engine.Allocate(1);
        engine.SetY(0, Cell.Atom(99));

        var code = BuildCode(Opcode.PutValueY, 0, 1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(99), engine.GetRegister(1));
    }

    // ---------- put_constant / put_atom / put_integer / put_nil ----------

    [Fact]
    public void PutConstant_SetsRegisterToAtom()
    {
        var (engine, interp) = NewEngine();
        var code = BuildCode(Opcode.PutConstant, 50, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(50), engine.GetRegister(0));
    }

    [Fact]
    public void PutAtom_SetsRegisterToAtom()
    {
        var (engine, interp) = NewEngine();
        var code = BuildCode(Opcode.PutAtom, 51, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(51), engine.GetRegister(0));
    }

    [Fact]
    public void PutInteger_SetsRegisterToInt()
    {
        var (engine, interp) = NewEngine();
        var code = BuildCode(Opcode.PutInteger, -7, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Int(-7), engine.GetRegister(0));
    }

    [Fact]
    public void PutNil_SetsRegisterToEmptyListAtom()
    {
        var (engine, interp) = NewEngine();
        var code = BuildCode(Opcode.PutNil, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(AtomTable.EmptyListId), engine.GetRegister(0));
    }

    // ---------- A1 / A2 consolidations ----------

    [Fact]
    public void GetConstantA1_TargetsRegister0()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(42));

        var code = BuildCode(Opcode.GetConstantA1, 42, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void GetConstantA1_MismatchOnReg0_Fails()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(99));

        var code = BuildCode(Opcode.GetConstantA1, 42, Opcode.Halt);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    [Fact]
    public void GetConstantA2_TargetsRegister1()
    {
        var (engine, interp) = NewEngine();
        engine.SetRegister(1, Cell.Atom(42));

        var code = BuildCode(Opcode.GetConstantA2, 42, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void PutConstantA1_SetsRegister0()
    {
        var (engine, interp) = NewEngine();
        var code = BuildCode(Opcode.PutConstantA1, 70, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(70), engine.GetRegister(0));
    }

    [Fact]
    public void PutConstantA2_SetsRegister1()
    {
        var (engine, interp) = NewEngine();
        var code = BuildCode(Opcode.PutConstantA2, 71, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(Cell.Atom(71), engine.GetRegister(1));
    }

    // ---------- Integration: a real Prolog-like predicate ----------

    [Fact]
    public void Integration_FactWithConstantArg_HeadMatchSucceeds()
    {
        // Prolog source:
        //   p(a).
        // ?- p(a).
        //
        // Bytecode:
        //   0..8:   put_atom 'a', X[0]
        //   9..17:  call p/1 at 19, num_live=0
        //   18:     halt
        //   19..27: get_atom 'a', X[0]       (p's head match)
        //   28:     proceed
        const int atomA = 100;
        var code = BuildCode(
            Opcode.PutAtom, atomA, 0,
            Opcode.Call, 19, 0,
            Opcode.Halt,
            Opcode.GetAtom, atomA, 0,
            Opcode.Proceed);

        var (engine, interp) = NewEngine();
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(18, engine.P);                 // halted at the post-call instruction
    }

    [Fact]
    public void Integration_FactWithMismatchedArg_HeadMatchFails()
    {
        // ?- p(a). | p(b).
        const int atomA = 100, atomB = 101;
        var code = BuildCode(
            Opcode.PutAtom, atomA, 0,
            Opcode.Call, 19, 0,
            Opcode.Halt,
            Opcode.GetAtom, atomB, 0,
            Opcode.Proceed);

        var (engine, interp) = NewEngine();
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    [Fact]
    public void Integration_CallerPassesVarCalleeBindsIt()
    {
        // ?- p(X). | p(a).
        // X should be bound to atom 'a' after the call returns.
        const int atomA = 100;
        var code = BuildCode(
            Opcode.PutVariableX, 1, 0,    // X[1] = X[0] = fresh var on heap
            Opcode.Call, 19, 0,
            Opcode.Halt,
            Opcode.GetAtom, atomA, 0,
            Opcode.Proceed);

        var (engine, interp) = NewEngine();
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Cell x1 = engine.GetRegister(1);
        Assert.Equal(Tag.Ref, x1.Tag);
        int derefed = engine.Deref(x1.AsHeapIndex);
        Assert.Equal(Cell.Atom(atomA), engine.GetHeap(derefed));
    }

    [Fact]
    public void Integration_PredicateWithPermanentSurvivesCall()
    {
        // p(X) :- q, r(X).
        // Permanent Y[0] holds X across the q/0 call so r can use it.
        //
        // ?- p(a).
        //
        // Bytecode (offsets carefully laid out):
        //   0:    put_atom 'a', X[0]            (9 bytes; PC after: 9)
        //   9:    call p/1=18                    (9 bytes; PC after: 18)
        //   18:   halt                           (1 byte)
        //   19:   allocate 1                     (5 bytes; PC after: 24)
        //   24:   get_variable_y Y[0], X[0]      (9 bytes; X comes from call, save into Y[0]; PC after 33)
        //   33:   call q/0=52                    (9 bytes; PC after: 42)
        //   42:   put_value_y Y[0], X[0]         (9 bytes; restore X into A[0]; PC after: 51)
        //   51:   deallocate                     (1 byte; PC after: 52)
        //   --- right past deallocate is q's start, which is fine: q is just a return-success
        //   52:   proceed                        (q/0 body: succeed and return; 1 byte)
        //   --- but actually the deallocate at 51 doesn't fall through to q — we want
        //       deallocate followed by execute r/1. Let me redesign.
        //
        // Redesign (allocating + tail-calling r):
        //   0:    put_atom 'a', X[0]             (9 → PC 9)
        //   9:    call p/1=18                     (9 → PC 18)
        //   18:   halt                            (1)
        //
        //   19:   allocate 1                      (5 → PC 24)            ; p enters
        //   24:   get_variable_y Y[0], X[0]       (9 → PC 33)            ; save X
        //   33:   call q/0=51                     (9 → PC 42)            ; non-tail call q
        //   42:   put_value_y Y[0], X[0]          (9 → PC 51)            ; restore X for r
        //   51:   deallocate                      (1 → PC 52)            ; can't, q is here. Let me move q.
        //
        // Even simpler: put q AFTER everything, and use execute instead of call for r so we
        // can skip the second deallocate. Final layout:
        //
        //   0:    put_atom 'a', X[0]             (9 → 9)
        //   9:    call p=18                       (9 → 18)
        //   18:   halt                            (1)
        //   19:   allocate 1                      (5 → 24)               ; p enters
        //   24:   get_variable_y Y[0], X[0]       (9 → 33)               ; save X
        //   33:   call q=53                       (9 → 42)
        //   42:   put_value_y Y[0], X[0]          (9 → 51)
        //   51:   deallocate                      (1 → 52)
        //   52:   execute r=54                    (5 → unreachable, jumps to 54)
        //   53:   proceed                         (q/0 body)
        //   54:   get_atom 'a', X[0]              (9 → 63)               ; r checks X
        //   63:   proceed                         (r returns)
        const int atomA = 100;
        var code = BuildCode(
            Opcode.PutAtom, atomA, 0,            // 0..8
            Opcode.Call, 19, 0,                  // 9..17
            Opcode.Halt,                         // 18
            Opcode.Allocate, 1,                  // 19..23
            Opcode.GetVariableY, 0, 0,           // 24..32
            Opcode.Call, 53, 0,                  // 33..41
            Opcode.PutValueY, 0, 0,              // 42..50
            Opcode.Deallocate,                   // 51
            Opcode.Execute, 54,                  // 52..56  -- wait, this goes past 53
            // Hmm offsets break. Let me redo positioning so q sits before execute.
            // Cleaner: drop the q call entirely. The test verifies permanent survives a
            // generic operation, not specifically a call. So just verify Y[0] still holds
            // X's heap REF after we walk through several opcodes.

            Opcode.Halt                          // padding
        );

        // Actually run a much simpler test: allocate, get_variable_y, mutate X[0],
        // put_value_y, check that X[0] now has the original (restored) value.
        var simpler = BuildCode(
            Opcode.Allocate, 1,                    // 0..4
            Opcode.GetVariableY, 0, 0,             // 5..13   Y[0] := X[0]
            Opcode.PutAtom, 999, 0,                // 14..22  X[0] := junk
            Opcode.PutValueY, 0, 0,                // 23..31  X[0] := Y[0]  (restored)
            Opcode.Deallocate,                     // 32
            Opcode.Halt                            // 33
        );

        var (engine, interp) = NewEngine();
        engine.SetRegister(0, Cell.Atom(atomA));
        Assert.Equal(InterpreterResult.Halted, interp.Run(simpler, 0));
        Assert.Equal(Cell.Atom(atomA), engine.GetRegister(0));
    }
}

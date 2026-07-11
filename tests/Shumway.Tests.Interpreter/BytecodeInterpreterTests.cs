using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Interpreter;

public class BytecodeInterpreterTests
{
    // ---------- Halt and the canary ----------

    [Fact]
    public void Run_HaltAlone_ReturnsHalted()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        byte[] code = { (byte)Opcode.Halt };

        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void Run_ReservedInvalid_Throws()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        byte[] code = { (byte)Opcode.ReservedInvalid };

        var ex = Assert.Throws<InvalidOperationException>(() => interp.Run(code, 0));
        Assert.Contains("reserved_invalid", ex.Message);
    }

    [Fact]
    public void Run_UnimplementedOpcode_Throws()
    {
        // The specialised arithmetic / comparison opcodes (unify_eq, is_op,
        // less_than, etc.) are reserved bytecode forms whose dispatch will
        // land in a later chunk; for now they fall through to the
        // default-throws branch.
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        byte[] code = { (byte)Opcode.UnifyEq };

        var ex = Assert.Throws<NotImplementedException>(() => interp.Run(code, 0));
        Assert.Contains("not implemented", ex.Message);
    }

    // ---------- Proceed ----------

    [Fact]
    public void Run_ProceedAtTopLevel_ReturnsHalted()
    {
        // CP defaults to -1, so a top-level proceed is "returned past the entry point".
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        byte[] code = { (byte)Opcode.Proceed };

        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void Run_ProceedWithCpSet_JumpsToCp()
    {
        // proceed at PC=0 jumps to CP=1, where we have a halt.
        var engine = new Activation();
        engine.SetCp(1);
        var interp = new BytecodeInterpreter(engine);
        byte[] code = { (byte)Opcode.Proceed, (byte)Opcode.Halt };

        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(1, engine.P);
    }

    // ---------- Call & Execute ----------

    [Fact]
    public void Run_CallThenProceed_ReturnsToTheInstructionAfterCall()
    {
        // Layout:
        //   0..8:  call target=10, num_live=0   (9 bytes)
        //   9:     halt                         (1 byte, return resumes here)
        //   10:    proceed                      (1 byte, the callee)
        var code = new byte[11];
        code[0] = (byte)Opcode.Call;
        BytecodeIO.WriteInt32(code, 1, value:10);
        BytecodeIO.WriteInt32(code, 5, value: 0);
        code[9] = (byte)Opcode.Halt;
        code[10] = (byte)Opcode.Proceed;

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        // Final PC is at the halt instruction.
        Assert.Equal(9, engine.P);
        // CP was set to the return address (9) at call time and not modified by proceed/halt.
        Assert.Equal(9, engine.Cp);
    }

    [Fact]
    public void Run_Execute_DoesNotChangeCp()
    {
        // execute target=5, then halt at 5. CP stays at whatever it was before.
        var code = new byte[6];
        code[0] = (byte)Opcode.Execute;
        BytecodeIO.WriteInt32(code, 1, value:5);
        code[5] = (byte)Opcode.Halt;

        var engine = new Activation();
        engine.SetCp(0x77);                    // sentinel value to verify execute does not overwrite
        var interp = new BytecodeInterpreter(engine);

        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(0x77, engine.Cp);          // unchanged
        Assert.Equal(5, engine.P);
    }

    [Fact]
    public void Run_ExecuteChain_TailCallsPreserveTheOriginalCp()
    {
        // Modelling the WAM tail-call pattern: main calls A, A executes B, B executes C,
        // and C proceeds. Since `execute` does NOT touch CP, the return address saved
        // by the original `call` survives the entire chain — the final proceed lands
        // right after the original call instruction.
        //
        //   0..8:   call A=10           (CP gets set to 9)
        //   9:      halt
        //   10..14: execute B=15        (A — CP unchanged)
        //   15..19: execute C=20        (B — CP unchanged)
        //   20:     proceed             (C — jumps back to CP=9)
        //
        // Note: a non-tail-call chain (call A → A calls B → B calls C) would NOT work
        // without intervening allocate/deallocate, because each `call` overwrites the
        // single _cp register. Env frames are how the WAM stacks return addresses; that
        // pattern is exercised by Run_CallerWithEnvFrameAndCallee below.
        var code = new byte[21];
        code[0] = (byte)Opcode.Call;
        BytecodeIO.WriteInt32(code, 1, 10);
        BytecodeIO.WriteInt32(code, 5, 0);
        code[9] = (byte)Opcode.Halt;

        code[10] = (byte)Opcode.Execute;
        BytecodeIO.WriteInt32(code, 11, 15);

        code[15] = (byte)Opcode.Execute;
        BytecodeIO.WriteInt32(code, 16, 20);

        code[20] = (byte)Opcode.Proceed;

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(9, engine.P);
        Assert.Equal(9, engine.Cp);              // never overwritten by the execute chain
    }

    // ---------- Allocate & Deallocate ----------

    [Fact]
    public void Run_AllocateThenDeallocate_RoundTripsEnvironmentState()
    {
        //   0..4:  allocate 2     (5 bytes)
        //   5:     deallocate     (1 byte)
        //   6:     halt
        var code = new byte[7];
        code[0] = (byte)Opcode.Allocate;
        BytecodeIO.WriteInt32(code, 1, 2);
        code[5] = (byte)Opcode.Deallocate;
        code[6] = (byte)Opcode.Halt;

        var engine = new Activation();
        engine.SetCp(0x42);                     // sentinel
        var interp = new BytecodeInterpreter(engine);

        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(-1, engine.E);              // env was popped
        Assert.Equal(0x42, engine.Cp);           // CP restored from the frame
        // Phase 28 env-frame trimming: with no choice point protecting the
        // just-popped frame (fresh engine, B = -1 < E = 0), deallocate reclaims
        // its EnvSize(2) = 5 slots, so the stack returns to empty. (Previously the
        // dropped slots lingered until a later op overwrote them.)
        Assert.Equal(0, engine.StackTop);
    }

    [Fact]
    public void Run_AllocateAdvancesPc()
    {
        // allocate 0 leaves only the 2 control slots and PC at instruction-after-allocate.
        var code = new byte[6];
        code[0] = (byte)Opcode.Allocate;
        BytecodeIO.WriteInt32(code, 1, 0);
        code[5] = (byte)Opcode.Halt;

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(5, engine.P);
        Assert.Equal(0, engine.E);               // env at top-of-stack
    }

    [Fact]
    public void Run_NestedAllocateAndDeallocate_PopsInLifoOrder()
    {
        //   0:  allocate 1
        //   5:  allocate 1
        //   10: deallocate     (inner)
        //   11: deallocate     (outer)
        //   12: halt
        var code = new byte[13];
        code[0] = (byte)Opcode.Allocate;
        BytecodeIO.WriteInt32(code, 1, 1);
        code[5] = (byte)Opcode.Allocate;
        BytecodeIO.WriteInt32(code, 6, 1);
        code[10] = (byte)Opcode.Deallocate;
        code[11] = (byte)Opcode.Deallocate;
        code[12] = (byte)Opcode.Halt;

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(-1, engine.E);
    }

    // ---------- Mixed: a tiny "predicate" exercising Allocate / Call / Deallocate / Proceed ----------

    [Fact]
    public void Run_CallerWithEnvFrameAndCallee_RoundTripsCleanState()
    {
        // Caller (entry):
        //   0:   allocate 1        (env frame)
        //   5:   call target=20    (callee)
        //   14:  deallocate
        //   15:  halt
        //
        // Callee (proceed):
        //   20:  proceed
        var code = new byte[21];
        code[0] = (byte)Opcode.Allocate;
        BytecodeIO.WriteInt32(code, 1, 1);

        code[5] = (byte)Opcode.Call;
        BytecodeIO.WriteInt32(code, 6, 20);
        BytecodeIO.WriteInt32(code, 10, 0);

        code[14] = (byte)Opcode.Deallocate;
        code[15] = (byte)Opcode.Halt;

        code[20] = (byte)Opcode.Proceed;

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(-1, engine.E);              // env popped
        Assert.Equal(15, engine.P);
    }

    // ---------- Validation ----------

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(100)]
    public void Run_StartPcOutOfRange_Throws(int startPc)
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        byte[] code = { (byte)Opcode.Halt };
        Assert.Throws<ArgumentOutOfRangeException>(() => interp.Run(code, startPc));
    }

    [Fact]
    public void Run_NullCode_Throws()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Throws<ArgumentNullException>(() => interp.Run(null!, 0));
    }

    [Fact]
    public void Ctor_NullEngine_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BytecodeInterpreter(null!));
    }

    [Fact]
    public void Run_PcJumpsToOutOfRange_Throws()
    {
        // execute jumps PC to 999, which is past the code's end.
        var code = new byte[5];
        code[0] = (byte)Opcode.Execute;
        BytecodeIO.WriteInt32(code, 1, 999);

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Throws<InvalidOperationException>(() => interp.Run(code, 0));
    }
}

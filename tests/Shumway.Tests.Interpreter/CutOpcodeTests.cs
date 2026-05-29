using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Interpreter;

public class CutOpcodeTests
{
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

    // ---------- B0 maintenance on call / execute ----------

    [Fact]
    public void Call_CapturesCurrentBIntoB0BeforeJumping()
    {
        // Set up an outer CP, then issue a call. The callee's B0 should match the outer CP's B.
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);          // outer CP at idx 0
        int outerB = engine.B;

        var code = BuildCode(
            Opcode.Call, 14, 0,                // 0..8 → target 14
            Opcode.Halt,                        // 9
            // padding so target lands at 14
            Opcode.Halt,                        // 10
            Opcode.Halt,                        // 11
            Opcode.Halt,                        // 12
            Opcode.Halt,                        // 13
            Opcode.Halt);                       // 14 — callee halts immediately
        var interp = new BytecodeInterpreter(engine);

        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(outerB, engine.B0);
    }

    [Fact]
    public void Execute_RefreshesB0()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);
        int outerB = engine.B;

        var code = BuildCode(
            Opcode.Execute, 5,                  // 0..4 → target 5
            Opcode.Halt);                       // 5
        var interp = new BytecodeInterpreter(engine);

        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(outerB, engine.B0);
    }

    // ---------- neck_cut ----------

    [Fact]
    public void NeckCut_DiscardsCpsAboveB0()
    {
        var engine = new Engine();
        engine.SetB0(-1);                        // simulate "procedure entered with no CP"
        engine.PushChoicePoint(0, 0);            // simulate try_me_else creating CP

        var code = BuildCode(Opcode.NeckCut, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(-1, engine.B);              // CP discarded back to B0
    }

    [Fact]
    public void NeckCut_PreservesCpsBelowB0()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);            // outer CP — pre-procedure
        int outerB = engine.B;
        engine.SetB0(outerB);                    // procedure entry saw this as B
        engine.PushChoicePoint(0, 0);            // inner CP — created inside the procedure

        var code = BuildCode(Opcode.NeckCut, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(outerB, engine.B);          // outer CP intact, inner discarded
    }

    [Fact]
    public void NeckCut_WithNoCps_IsNoOp()
    {
        var engine = new Engine();
        engine.SetB0(-1);
        var code = BuildCode(Opcode.NeckCut, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(-1, engine.B);
    }

    // ---------- get_level + cut ----------

    [Fact]
    public void GetLevel_SavesB0IntoY()
    {
        // get_level captures _b0 (the procedure-entry cut barrier), not the
        // current _b. The Y slot keeps that value across sub-goal calls that
        // overwrite the engine's B0 register.
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);            // CPs the cut should discard
        engine.SetB0(-1);                         // simulate "procedure entry saw no CPs"
        engine.Allocate(1);

        var code = BuildCode(Opcode.GetLevel, 0, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        // get_level stores the barrier as a RawInt control word (ADR-016).
        Assert.Equal(-1, (int)engine.GetY(0).Data);  // _b0 was -1, saved into Y[0]
    }

    [Fact]
    public void CutOpcode_CutsToBarrierStoredInY()
    {
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);            // outer CP
        int outerB = engine.B;
        engine.Allocate(1);
        engine.SetY(0, Cell.RawInt(outerB));     // pretend get_level captured outerB
        engine.PushChoicePoint(0, 0);            // inner CP

        var code = BuildCode(Opcode.Cut, 0, Opcode.Halt);
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(outerB, engine.B);
    }

    [Fact]
    public void GetLevelThenCut_RoundTrips()
    {
        // Sets up: outer CP exists at B = outerB, then "procedure entered" with
        // _b0 = outerB. get_level captures _b0 into Y[0]. An inner CP is pushed
        // (a sub-goal CP). Cut Y[0] should discard the inner but keep outer.
        var engine = new Engine();
        engine.PushChoicePoint(0, 0);
        int outerB = engine.B;
        engine.SetB0(outerB);
        engine.Allocate(1);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(BuildCode(Opcode.GetLevel, 0, Opcode.Halt), 0));
        Assert.Equal(outerB, (int)engine.GetY(0).Data);

        // Push an inner CP — simulating a sub-goal's try_me_else.
        engine.PushChoicePoint(0, 0);
        Assert.Equal(InterpreterResult.Halted, interp.Run(BuildCode(Opcode.Cut, 0, Opcode.Halt), 0));
        Assert.Equal(outerB, engine.B);                  // inner discarded, outer preserved
    }

    // ---------- Integration: clause with neck_cut commits ----------

    [Fact]
    public void Integration_FirstClauseWithNeckCut_CommitsAndDiscardsAlternatives()
    {
        // Prolog:
        //   p(a) :- !.
        //   p(b).
        //   p(c).
        //   ?- p(X).
        //
        // Expected outcome: X is bound to 'a' (clause 1 matched); the choice point
        // created by try_me_else for retrying p(b)/p(c) is discarded by the cut.
        //
        // Bytecode layout:
        //   0..8:   put_variable_x 1, 0      ; X[1] = X[0] = fresh heap unbound
        //   9..17:  call p=19, 0
        //   18:     halt
        //
        //   p_entry at 19:
        //   19..27: try_me_else 39, 1
        //   28..36: get_atom 100, X[0]       ; clause 1 head: p(a)
        //   37:     neck_cut
        //   38:     proceed
        //
        //   clause2 at 39 (unreachable due to cut):
        //   39..43: retry_me_else 54
        //   44..52: get_atom 101, X[0]
        //   53:     proceed
        //
        //   clause3 at 54 (unreachable):
        //   54:     trust_me
        //   55..63: get_atom 102, X[0]
        //   64:     proceed
        var code = BuildCode(
            Opcode.PutVariableX, 1, 0,        // 0..8
            Opcode.Call, 19, 0,               // 9..17
            Opcode.Halt,                      // 18

            Opcode.TryMeElse, 39, 1,          // 19..27
            Opcode.GetAtom, 100, 0,           // 28..36
            Opcode.NeckCut,                   // 37
            Opcode.Proceed,                   // 38

            Opcode.RetryMeElse, 54,           // 39..43
            Opcode.GetAtom, 101, 0,           // 44..52
            Opcode.Proceed,                   // 53

            Opcode.TrustMe,                   // 54
            Opcode.GetAtom, 102, 0,           // 55..63
            Opcode.Proceed);                  // 64

        var engine = new Engine();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        // CP was discarded by neck_cut.
        Assert.Equal(-1, engine.B);

        // X (held by X[1]) is bound to atom 'a' = 100.
        Cell x1 = engine.GetRegister(1);
        Assert.Equal(Tag.Ref, x1.Tag);
        int derefed = engine.Deref(x1.AsHeapIndex);
        Assert.Equal(Cell.Atom(100), engine.GetHeap(derefed));
    }
}

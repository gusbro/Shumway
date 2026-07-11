using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Interpreter;

public class ChoicePointOpcodeTests
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

    // ---------- Choice-point opcodes ----------

    [Fact]
    public void TryMeElse_PushesChoicePointWithGivenBpAndArity()
    {
        var engine = new Activation();
        engine.SetRegister(0, Cell.Atom(42));
        var interp = new BytecodeInterpreter(engine);

        // try_me_else BP=14, arity=1; halt.
        var code = BuildCode(Opcode.TryMeElse, 14, 1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        // A CP must be active.
        Assert.True(engine.B >= 0);
        int arity = (int)engine.GetStack(engine.B + Activation.CpArityOffset).Data;
        int bp = (int)engine.GetStack(engine.B + Activation.CpBpOffset(arity)).Data;
        Assert.Equal(1, arity);
        Assert.Equal(14, bp);
        // Saved A1 matches X[0] at push.
        Assert.Equal(Cell.Atom(42), engine.GetStack(engine.B + Activation.CpArg1Offset));
    }

    [Fact]
    public void RetryMeElse_RestoresStateAndUpdatesBp()
    {
        var engine = new Activation();
        engine.SetRegister(0, Cell.Atom(7));
        // Manually set up a CP so retry has something to restore from.
        engine.PushChoicePoint(arity: 1, nextClauseAddr: 0x100);

        // After the push, mutate X[0]. retry_me_else must roll it back.
        engine.SetRegister(0, Cell.Atom(99));

        var interp = new BytecodeInterpreter(engine);
        var code = BuildCode(Opcode.RetryMeElse, 0x200, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Assert.Equal(Cell.Atom(7), engine.GetRegister(0));  // restored
        // BP was updated.
        int bp = (int)engine.GetStack(engine.B + Activation.CpBpOffset(1)).Data;
        Assert.Equal(0x200, bp);
        Assert.True(engine.B >= 0);                          // CP preserved
    }

    [Fact]
    public void TrustMe_RestoresStateAndDiscardsCp()
    {
        var engine = new Activation();
        engine.SetRegister(0, Cell.Atom(3));
        engine.PushChoicePoint(arity: 1, nextClauseAddr: 0x100);
        engine.SetRegister(0, Cell.Atom(77));

        var interp = new BytecodeInterpreter(engine);
        var code = BuildCode(Opcode.TrustMe, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Assert.Equal(Cell.Atom(3), engine.GetRegister(0));  // restored
        Assert.Equal(-1, engine.B);                          // CP discarded
    }

    // ---------- Backtrack on failure ----------

    [Fact]
    public void Fail_WithActiveCp_BacktracksToBp()
    {
        // Layout:
        //   0..8:   put_atom 99, X[0]                     ; set X[0] = Atom(99)
        //   9..17:  try_me_else BP=28, arity=0
        //   18..26: get_atom 100, X[0]                    ; will FAIL (99 vs 100)
        //   27:     halt                                  ; success branch (never taken)
        //   28:     halt                                  ; backtrack target
        var code = BuildCode(
            Opcode.PutAtom, 99, 0,
            Opcode.TryMeElse, 28, 0,
            Opcode.GetAtom, 100, 0,
            Opcode.Halt,
            Opcode.Halt);

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(28, engine.P);                         // landed on the backtrack target
    }

    [Fact]
    public void Fail_WithoutCp_ReturnsFailed()
    {
        // Same shape but without try_me_else — no CP, so failure is final.
        var code = BuildCode(
            Opcode.PutAtom, 99, 0,
            Opcode.GetAtom, 100, 0,
            Opcode.Halt);

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    // ---------- Multi-clause integration ----------

    /// <summary>
    /// Models the Prolog program
    /// <code>
    /// p(a).
    /// p(b).
    /// p(c).
    /// ?- p(X).
    /// </code>
    /// X is set by the caller (via put_atom) to one of 100/101/102/103 corresponding to
    /// a/b/c/d. The 64-byte bytecode chains try_me_else → retry_me_else → trust_me
    /// across the three clause heads.
    /// </summary>
    private static byte[] BuildMultiClauseFixture() => BuildCode(
        // Caller (offsets 0..18)
        Opcode.PutAtom, 0xCAFE, 0,    // 0..8   — placeholder atom id; tests overwrite X[0] instead
        Opcode.Call, 19, 0,           // 9..17
        Opcode.Halt,                  // 18

        // p_entry at 19
        Opcode.TryMeElse, 38, 1,      // 19..27 — next clause at 38, arity 1
        Opcode.GetAtom, 100, 0,       // 28..36 — clause 1 head: p(a)
        Opcode.Proceed,               // 37

        // clause 2 at 38
        Opcode.RetryMeElse, 53,       // 38..42 — next clause at 53
        Opcode.GetAtom, 101, 0,       // 43..51 — clause 2 head: p(b)
        Opcode.Proceed,               // 52

        // clause 3 at 53
        Opcode.TrustMe,               // 53
        Opcode.GetAtom, 102, 0,       // 54..62 — clause 3 head: p(c)
        Opcode.Proceed                // 63
    );

    [Fact]
    public void MultiClause_QueryWithMatchingArgInFirstClause_Succeeds()
    {
        // ?- p(a). — should match clause 1 on the first try.
        var code = BuildMultiClauseFixture();
        var engine = new Activation();
        engine.SetRegister(0, Cell.Atom(100));        // pre-load X[0] with 'a'
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 9));     // start at the call
        Assert.Equal(18, engine.P);                   // halted at post-call instruction
        Assert.True(engine.B >= 0);                   // CP from try_me_else is still alive
    }

    [Fact]
    public void MultiClause_QueryWithMatchingArgInMiddleClause_BacktracksOnce()
    {
        // ?- p(b). — clause 1 fails, retry_me_else moves to clause 2 which matches.
        var code = BuildMultiClauseFixture();
        var engine = new Activation();
        engine.SetRegister(0, Cell.Atom(101));
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 9));
        Assert.Equal(18, engine.P);
    }

    [Fact]
    public void MultiClause_QueryWithMatchingArgInLastClause_BacktracksTwiceAndDiscardsCp()
    {
        // ?- p(c). — both retry paths fail, trust_me lands clause 3 which matches.
        var code = BuildMultiClauseFixture();
        var engine = new Activation();
        engine.SetRegister(0, Cell.Atom(102));
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 9));
        Assert.Equal(18, engine.P);
        Assert.Equal(-1, engine.B);                   // trust_me discarded the CP
    }

    [Fact]
    public void MultiClause_QueryWithNoMatchingClause_ExhaustsAndFails()
    {
        // ?- p(d). — no clause matches; trust_me discards the CP and the final fail
        // has no CP to backtrack to, so the interpreter reports Failed.
        var code = BuildMultiClauseFixture();
        var engine = new Activation();
        engine.SetRegister(0, Cell.Atom(103));        // 'd' — unknown
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 9));
        Assert.Equal(-1, engine.B);                   // CP exhausted
    }

    [Fact]
    public void Backtrack_RestoresBindingsMadeAfterPush()
    {
        // X[0] is initially an unbound variable. First clause binds it to atom 'a' and
        // then fails on a subsequent get_atom; retry must roll the binding back so the
        // second clause sees the original unbound X[0] again and can bind it to 'b'.
        //
        //   0..8:   try_me_else 24, 1                ; next clause at 24, arity 1
        //   9..17:  get_atom 'a'=100, X[0]           ; binds X[0] := Atom(100)
        //   18..22: get_atom 'b'=101, X[0]           ; FAILS (X[0] is now 100, not 101)
        //                                            ; wait — get_atom is 9 bytes not 5
        // Let me recompute:
        //   0..8:   try_me_else 28, 1
        //   9..17:  get_atom 100, X[0]
        //   18..26: get_atom 101, X[0]    ; fails — X[0] was just bound to 100
        //   27:     halt                  ; success branch (never reached)
        //   28..32: retry_me_else 42
        //   33..41: get_atom 101, X[0]    ; succeeds — X[0] is now unbound again
        //   42:     halt
        var code = BuildCode(
            Opcode.TryMeElse, 28, 1,      // 0..8
            Opcode.GetAtom, 100, 0,       // 9..17
            Opcode.GetAtom, 101, 0,       // 18..26   FAILS
            Opcode.Halt,                  // 27
            Opcode.RetryMeElse, 42,       // 28..32
            Opcode.GetAtom, 101, 0,       // 33..41
            Opcode.Halt);                 // 42

        var engine = new Activation();
        int x0 = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(x0));
        // The CP needs Hb to be below x0 for the binding to be trailed, otherwise the
        // retry won't unwind it.
        engine.SetHbForTesting(engine.HeapTop);

        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(42, engine.P);

        // Final value of X: bound to 101 ('b'). Deref through the REF.
        int derefedAddr = engine.Deref(x0);
        Assert.Equal(Cell.Atom(101), engine.GetHeap(derefedAddr));
    }
}

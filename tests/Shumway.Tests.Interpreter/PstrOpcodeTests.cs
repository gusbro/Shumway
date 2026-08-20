using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Interpreter;

public class PstrOpcodeTests
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

    // ---------- put_pstr ----------

    [Fact]
    public void PutPstr_SetsRegisterToRefAtPstrHeader()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "hello" });

        var code = BuildCode(Opcode.PutPstr, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Cell x0 = engine.GetRegister(0);
        Assert.Equal(Tag.Ref, x0.Tag);

        int headerIdx = x0.AsHeapIndex;
        Cell header = engine.GetHeap(headerIdx);
        Assert.Equal(Tag.Pstr, header.Tag);
        Assert.Equal(5, header.AsPstrLength);
        Assert.Equal("hello", engine.AsPstrString(headerIdx));
    }

    [Fact]
    public void PutPstr_EmptyStringLiteral_ProducesZeroLengthPstr()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "" });

        var code = BuildCode(Opcode.PutPstr, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Cell header = engine.GetHeap(engine.GetRegister(0).AsHeapIndex);
        Assert.Equal(0, header.AsPstrLength);
    }

    [Fact]
    public void GetPstr_OnUnboundRegister_BindsItToTheLiteral()
    {
        var engine = new Activation();
        int x0 = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(x0));

        var interp = new BytecodeInterpreter(engine, new[] { "hi" });
        var code = BuildCode(Opcode.GetPstr, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        // The unbound var now derefs to a PSTR header carrying "hi". Deref follows
        // x0 → heap[x0] = Ref(headerIdx) → headerIdx, where heap[headerIdx] is the
        // PSTR cell itself (a non-REF, so the chain stops there).
        int dereffed = engine.Deref(x0);
        Cell tgt = engine.GetHeap(dereffed);
        Assert.Equal(Tag.Pstr, tgt.Tag);
        Assert.Equal("hi", engine.AsPstrString(dereffed));
    }

    [Fact]
    public void GetPstr_AgainstMatchingPstr_Succeeds()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "abc" });

        // put_pstr "abc" → X[0], then get_pstr "abc" against X[0] should re-unify cleanly.
        var code = BuildCode(
            Opcode.PutPstr, 0, 0,
            Opcode.GetPstr, 0, 0,
            Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
    }

    [Fact]
    public void GetPstr_AgainstMismatchedPstr_Fails()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "abc", "xyz" });

        var code = BuildCode(
            Opcode.PutPstr, 0, 0,           // X[0] := "abc"
            Opcode.GetPstr, 1, 0,           // try to match X[0] against "xyz"
            Opcode.Halt);
        Assert.Equal(InterpreterResult.Failed, interp.Run(code, 0));
    }

    [Fact]
    public void GetPstr_LiteralIdOutOfRange_Throws()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, Array.Empty<string>());

        var code = BuildCode(Opcode.GetPstr, 5, 0, Opcode.Halt);
        var ex = Assert.Throws<InvalidOperationException>(() => interp.Run(code, 0));
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void DefaultConstructor_PoolIsEmpty_PstrOpcodesThrowOnUse()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);          // no pool

        var code = BuildCode(Opcode.PutPstr, 0, 0, Opcode.Halt);
        Assert.Throws<InvalidOperationException>(() => interp.Run(code, 0));
    }

    // ---------- unify_pstr_head ----------

    [Fact]
    public void UnifyPstrHead_DecomposesFirstCodeUnitAndAdvancesCursor()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "ab" });

        // Build "ab" PSTR, then point the unify cursor at the header and decompose.
        // Allocate a heap slot for the cursor cell and copy the PSTR header into it.
        var build = BuildCode(Opcode.PutPstr, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(build, 0));

        int pstrHdrIdx = engine.GetRegister(0).AsHeapIndex;
        int cursorIdx = engine.AllocateHeap(1);
        engine.SetHeap(cursorIdx, engine.GetHeap(pstrHdrIdx));   // copy the PSTR header
        engine.SetUnifyPointer(cursorIdx);
        engine.SetWriteMode(false);

        var step = BuildCode(Opcode.UnifyPstrHead, 1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(step, 0));

        // X[1] is the first code unit as Int (codes mode).
        Assert.Equal(Cell.Int('a'), engine.GetRegister(1));

        // Cursor cell now holds the advanced PSTR header (length 1, content "b").
        Cell advanced = engine.GetHeap(cursorIdx);
        Assert.Equal(Tag.Pstr, advanced.Tag);
        Assert.Equal(1, advanced.AsPstrLength);
    }

    [Fact]
    public void UnifyPstrHead_LastCodeUnit_ReplacesCursorWithPstrTail()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "a" });
        var build = BuildCode(Opcode.PutPstr, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(build, 0));

        int pstrHdrIdx = engine.GetRegister(0).AsHeapIndex;
        int cursorIdx = engine.AllocateHeap(1);
        engine.SetHeap(cursorIdx, engine.GetHeap(pstrHdrIdx));
        engine.SetUnifyPointer(cursorIdx);
        engine.SetWriteMode(false);

        var step = BuildCode(Opcode.UnifyPstrHead, 1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(step, 0));

        Assert.Equal(Cell.Int('a'), engine.GetRegister(1));
        // After consuming the last char, the cursor holds the PSTR's tail value — for
        // a fresh PSTR built by MakePstr that's the empty-list atom.
        Cell tail = engine.GetHeap(cursorIdx);
        Assert.Equal(Cell.Atom(AtomTable.EmptyListId), tail);
    }

    [Fact]
    public void UnifyPstrHead_OnEmptyOrNonPstr_Fails()
    {
        // Empty PSTR case: build "" then try to decompose.
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "" });
        var build = BuildCode(Opcode.PutPstr, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(build, 0));

        int pstrHdrIdx = engine.GetRegister(0).AsHeapIndex;
        int cursorIdx = engine.AllocateHeap(1);
        engine.SetHeap(cursorIdx, engine.GetHeap(pstrHdrIdx));
        engine.SetUnifyPointer(cursorIdx);
        engine.SetWriteMode(false);

        var step = BuildCode(Opcode.UnifyPstrHead, 1, Opcode.Halt);
        Assert.Equal(InterpreterResult.Failed, interp.Run(step, 0));
    }

    [Fact]
    public void UnifyPstrHead_ChainOfThreeSteps_ProducesEachCodeUnitInOrder()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "xyz" });
        var build = BuildCode(Opcode.PutPstr, 0, 0, Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(build, 0));

        int pstrHdrIdx = engine.GetRegister(0).AsHeapIndex;
        int cursorIdx = engine.AllocateHeap(1);
        engine.SetHeap(cursorIdx, engine.GetHeap(pstrHdrIdx));
        engine.SetUnifyPointer(cursorIdx);
        engine.SetWriteMode(false);

        // unify_pstr_head X[1]; unify_pstr_head X[2]; unify_pstr_head X[3]; halt
        var code = BuildCode(
            Opcode.UnifyPstrHead, 1,
            Opcode.UnifyPstrHead, 2,
            Opcode.UnifyPstrHead, 3,
            Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Assert.Equal(Cell.Int('x'), engine.GetRegister(1));
        Assert.Equal(Cell.Int('y'), engine.GetRegister(2));
        Assert.Equal(Cell.Int('z'), engine.GetRegister(3));
        // After the last step the cursor holds the tail (Atom([])).
        Assert.Equal(Cell.Atom(AtomTable.EmptyListId), engine.GetHeap(cursorIdx));
    }

    // ---------- Backtrack-on-fail ----------

    [Fact]
    public void GetPstr_FailureBacktracksToBp()
    {
        // put_pstr "abc"; try_me_else BP; get_pstr "xyz" (fail); halt; halt(BP)
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine, new[] { "abc", "xyz" });

        var code = BuildCode(
            Opcode.PutPstr, 0, 0,           // 0..8
            Opcode.TryMeElse, 28, 0,        // 9..17 (BP=28)
            Opcode.GetPstr, 1, 0,           // 18..26  — fails
            Opcode.Halt,                    // 27 (success branch)
            Opcode.Halt);                   // 28 (BP target)

        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));
        Assert.Equal(28, engine.P);
    }

    // ---------- The two list cursors agree on a chars PSTR (ADR-047) ----------
    //
    // These are the two paths that drifted apart: a callee head matching [H|T]
    // runs get_list, an inline list pattern compiles to a unify_list run. When
    // only one of them knew about PSTR, the same unification succeeded through
    // one and failed through the other. Nothing produces a chars PSTR from
    // Prolog yet (the literal pool is codes until the flag flips), so the
    // header is planted directly.

    private static int PlantCharsPstr(Activation engine, string text)
        => engine.MakePstr(text, TextKind.Chars);

    [Fact]
    public void GetList_OnACharsPstr_YieldsACharAtomHead()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        engine.SetRegister(0, Cell.Ref(PlantCharsPstr(engine, "ab")));

        // get_list X0 ; unify_variable X1 (head) ; unify_variable X2 (tail)
        var code = BuildCode(
            Opcode.GetList, 0,
            Opcode.UnifyVariableX, 1,
            Opcode.UnifyVariableX, 2,
            Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Cell head = engine.GetRegister(1);
        Assert.Equal(Tag.Atom, head.Tag);
        Assert.Equal("a", AtomTable.GetById(head.AsAtomId)!.Name);
    }

    [Fact]
    public void UnifyList_OnACharsPstr_YieldsTheSameHeadAsGetList()
    {
        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);

        int cursorIdx = engine.AllocateHeap(1);
        engine.SetHeap(cursorIdx, engine.GetHeap(PlantCharsPstr(engine, "ab")));
        engine.SetUnifyPointer(cursorIdx);
        engine.SetWriteMode(false);

        var code = BuildCode(
            Opcode.UnifyList,
            Opcode.UnifyVariableX, 1,
            Opcode.UnifyVariableX, 2,
            Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, 0));

        Cell head = engine.GetRegister(1);
        Assert.Equal(Tag.Atom, head.Tag);
        Assert.Equal("a", AtomTable.GetById(head.AsAtomId)!.Name);
    }
}

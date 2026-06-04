using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

public class CompoundHeadTests
{
    private static CompiledClause CompileSource(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        Assert.Single(clauses);
        return new ClauseCompiler().Compile(clauses[0]);
    }

    private record struct DisasmRow(Opcode Opcode, int[] Operands);

    private static List<DisasmRow> Disassemble(byte[] code)
    {
        var rows = new List<DisasmRow>();
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            var info = OpcodeTable.Get(op);
            var operands = new int[info.NumOperands];
            for (int i = 0; i < info.NumOperands; i++)
                operands[i] = BytecodeIO.ReadInt32(code, pc + 1 + i * 4);
            rows.Add(new DisasmRow(op, operands));
            pc += info.Size;
        }
        return rows;
    }

    // ---------- Single-level compound ----------

    [Fact]
    public void Compound_SingleArgAtom_EmitsGetStructureAndUnifyAtom()
    {
        // p(foo(a)).
        var cc = CompileSource("p(foo(a)).");
        int atomFoo = AtomTable.Intern("foo", permanent: true).Id;
        int functorFoo1 = FunctorTable.Intern(atomFoo, 1);
        int atomA = AtomTable.Intern("a", permanent: true).Id;

        var d = Disassemble(cc.Bytecode);
        Assert.Equal(3, d.Count);
        Assert.Equal(Opcode.GetStructure, d[0].Opcode);
        Assert.Equal(functorFoo1, d[0].Operands[0]);
        Assert.Equal(0, d[0].Operands[1]);                 // X[0]
        Assert.Equal(Opcode.UnifyAtom, d[1].Opcode);
        Assert.Equal(atomA, d[1].Operands[0]);
        Assert.Equal(Opcode.Proceed, d[2].Opcode);
    }

    [Fact]
    public void Compound_MultipleArgs_EmitsOneUnifyPerArg()
    {
        var cc = CompileSource("p(foo(a, 7, X)).");
        var d = Disassemble(cc.Bytecode);

        // get_structure foo/3, X[0]; unify_atom 'a'; unify_integer 7; unify_variable_x N; proceed
        Assert.Equal(5, d.Count);
        Assert.Equal(Opcode.GetStructure, d[0].Opcode);
        Assert.Equal(Opcode.UnifyAtom, d[1].Opcode);
        Assert.Equal(Opcode.UnifyInteger, d[2].Opcode);
        Assert.Equal(7, d[2].Operands[0]);
        Assert.Equal(Opcode.UnifyVariableX, d[3].Opcode);
        Assert.Equal(Opcode.Proceed, d[4].Opcode);
    }

    [Fact]
    public void Compound_AnonymousArg_EmitsUnifyVoid()
    {
        var cc = CompileSource("p(foo(_)).");
        var d = Disassemble(cc.Bytecode);
        Assert.Equal(Opcode.GetStructure, d[0].Opcode);
        Assert.Equal(Opcode.UnifyVoid, d[1].Opcode);
        Assert.Equal(1, d[1].Operands[0]);
    }

    [Fact]
    public void Compound_RepeatedVariable_EmitsUnifyValueOnSecondOccurrence()
    {
        var cc = CompileSource("p(foo(X, X)).");
        var d = Disassemble(cc.Bytecode);

        // get_structure foo/2; unify_variable_x N (first X); unify_value_x N (second X); proceed
        Assert.Equal(4, d.Count);
        Assert.Equal(Opcode.UnifyVariableX, d[1].Opcode);
        Assert.Equal(Opcode.UnifyValueX, d[2].Opcode);
        Assert.Equal(d[1].Operands[0], d[2].Operands[0]);   // same slot
    }

    // ---------- Nested compounds ----------

    [Fact]
    public void NestedCompound_GeneratesLayeredBytecode()
    {
        // p(foo(bar(x), y)). Expected layering:
        //   get_structure foo/2, X[0]
        //   unify_variable_x X[1]      ; defer bar(x)
        //   unify_atom 'y'
        //   get_structure bar/1, X[1]
        //   unify_atom 'x'
        //   proceed
        var cc = CompileSource("p(foo(bar(x), y)).");
        var d = Disassemble(cc.Bytecode);

        int atomFoo = AtomTable.Intern("foo", permanent: true).Id;
        int atomBar = AtomTable.Intern("bar", permanent: true).Id;
        int atomX = AtomTable.Intern("x", permanent: true).Id;
        int atomY = AtomTable.Intern("y", permanent: true).Id;

        Assert.Equal(6, d.Count);
        Assert.Equal(Opcode.GetStructure, d[0].Opcode);
        Assert.Equal(FunctorTable.Intern(atomFoo, 2), d[0].Operands[0]);
        Assert.Equal(Opcode.UnifyVariableX, d[1].Opcode);
        int barSlot = d[1].Operands[0];
        Assert.Equal(Opcode.UnifyAtom, d[2].Opcode);
        Assert.Equal(atomY, d[2].Operands[0]);
        Assert.Equal(Opcode.GetStructure, d[3].Opcode);
        Assert.Equal(FunctorTable.Intern(atomBar, 1), d[3].Operands[0]);
        Assert.Equal(barSlot, d[3].Operands[1]);
        Assert.Equal(Opcode.UnifyAtom, d[4].Opcode);
        Assert.Equal(atomX, d[4].Operands[0]);
        Assert.Equal(Opcode.Proceed, d[5].Opcode);
    }

    [Fact]
    public void RepeatedVariableAcrossNestedLayers_StaysInOneSlot()
    {
        // p(foo(X, bar(X))). The second X is inside bar(...) on layer 2.
        // The compiler must remember X's slot across worklist iterations.
        var cc = CompileSource("p(foo(X, bar(X))).");
        var d = Disassemble(cc.Bytecode);

        // ADR-019: bar(X) is foo's LAST arg → matched inline with
        // unify_structure (no temp + second get_structure).
        //   get_structure foo/2, X[0]
        //   unify_variable_x X[N]    ; first X — claim slot N
        //   unify_structure bar/1    ; last arg, inline
        //   unify_value_x X[N]       ; second X — same slot as first
        //   proceed
        Assert.Equal(Opcode.UnifyVariableX, d[1].Opcode);
        int xSlot = d[1].Operands[0];
        Assert.Equal(Opcode.UnifyStructure, d[2].Opcode);
        Assert.Equal(Opcode.UnifyValueX, d[3].Opcode);
        Assert.Equal(xSlot, d[3].Operands[0]);
    }

    // ---------- Lists ----------

    [Fact]
    public void List_SingleElement_EmitsGetListAndUnifyNil()
    {
        // p([a]). → '.'(a, []) at X[0]
        var cc = CompileSource("p([a]).");
        var d = Disassemble(cc.Bytecode);

        int atomA = AtomTable.Intern("a", permanent: true).Id;
        // get_list X[0]; unify_atom 'a'; unify_nil; proceed
        Assert.Equal(4, d.Count);
        Assert.Equal(Opcode.GetList, d[0].Opcode);
        Assert.Equal(0, d[0].Operands[0]);
        Assert.Equal(Opcode.UnifyAtom, d[1].Opcode);
        Assert.Equal(atomA, d[1].Operands[0]);
        Assert.Equal(Opcode.UnifyNil, d[2].Opcode);
        Assert.Equal(Opcode.Proceed, d[3].Opcode);
    }

    [Fact]
    public void List_HeadAndTailVariables_EmitsTwoUnifyVariableX()
    {
        // p([H | T]).
        var cc = CompileSource("p([H | T]).");
        var d = Disassemble(cc.Bytecode);

        // get_list X[0]; unify_variable_x X[H_slot]; unify_variable_x X[T_slot]; proceed
        Assert.Equal(4, d.Count);
        Assert.Equal(Opcode.GetList, d[0].Opcode);
        Assert.Equal(Opcode.UnifyVariableX, d[1].Opcode);
        Assert.Equal(Opcode.UnifyVariableX, d[2].Opcode);
        Assert.NotEqual(d[1].Operands[0], d[2].Operands[0]);
        Assert.Equal(Opcode.Proceed, d[3].Opcode);
    }

    [Fact]
    public void List_TwoElements_LayeredAsNestedCons()
    {
        // p([a, b]). → '.'(a, '.'(b, [])).  ADR-019: the nested cons (the list
        // tail) is matched inline with unify_list — no temp + second get_list.
        var cc = CompileSource("p([a, b]).");
        var d = Disassemble(cc.Bytecode);

        Assert.Equal(Opcode.GetList, d[0].Opcode);     // [a | _]
        Assert.Equal(Opcode.UnifyAtom, d[1].Opcode);   // a
        Assert.Equal(Opcode.UnifyList, d[2].Opcode);   // tail = [b | _], inline
        Assert.Equal(Opcode.UnifyAtom, d[3].Opcode);   // b
        Assert.Equal(Opcode.UnifyNil, d[4].Opcode);    // []
        Assert.Equal(Opcode.Proceed, d[5].Opcode);
    }

    // ---------- End-to-end: parse → compile → run ----------

    [Fact]
    public void EndToEnd_CompoundHead_MatchingCallSucceeds()
    {
        // Compile 'p(foo(a)).' and call it with a caller that builds foo(a) and
        // passes it as X[0].
        var cc = CompileSource("p(foo(a)).");
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        int atomFoo = AtomTable.Intern("foo", permanent: true).Id;
        int functorFoo1 = FunctorTable.Intern(atomFoo, 1);

        // Caller bytecode:
        //   put_structure foo/1, X[0]
        //   unify_atom 'a'
        //   call <clause>
        //   halt
        // (followed by clause bytes appended)
        var emitter = new BytecodeEmitter();
        emitter.EmitPutStructure(functorFoo1, 0);
        emitter.EmitUnifyAtom(atomA);
        int callPos = emitter.Position;
        emitter.EmitCall(targetAddress: 0, numLivePermanents: 0);
        emitter.EmitHalt();
        int clauseStart = emitter.Position;

        byte[] prefix = emitter.ToBytes();
        byte[] full = new byte[prefix.Length + cc.Bytecode.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(cc.Bytecode, 0, full, prefix.Length, cc.Bytecode.Length);
        BytecodeIO.WriteInt32(full, callPos + 1, clauseStart);

        var engine = new Engine();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(full, 0));
    }

    [Fact]
    public void EndToEnd_CompoundHead_MismatchedFunctor_Fails()
    {
        // Compile 'p(foo(a)).' but call with bar(a). Functor mismatch → Failed.
        var cc = CompileSource("p(foo(a)).");
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        int atomBar = AtomTable.Intern("bar", permanent: true).Id;
        int functorBar1 = FunctorTable.Intern(atomBar, 1);

        var emitter = new BytecodeEmitter();
        emitter.EmitPutStructure(functorBar1, 0);   // wrong functor
        emitter.EmitUnifyAtom(atomA);
        int callPos = emitter.Position;
        emitter.EmitCall(targetAddress: 0, numLivePermanents: 0);
        emitter.EmitHalt();
        int clauseStart = emitter.Position;

        byte[] prefix = emitter.ToBytes();
        byte[] full = new byte[prefix.Length + cc.Bytecode.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(cc.Bytecode, 0, full, prefix.Length, cc.Bytecode.Length);
        BytecodeIO.WriteInt32(full, callPos + 1, clauseStart);

        var engine = new Engine();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Failed, interp.Run(full, 0));
    }

    [Fact]
    public void EndToEnd_NestedCompound_RoundTrips()
    {
        // Compile 'p(foo(bar(x))).' and call it with a caller that builds the
        // matching nested structure. Verifies the two-pass BFS expansion
        // bytecode actually executes correctly against a real nested heap term.
        var cc = CompileSource("p(foo(bar(x))).");
        int atomFoo = AtomTable.Intern("foo", permanent: true).Id;
        int atomBar = AtomTable.Intern("bar", permanent: true).Id;
        int atomX = AtomTable.Intern("x", permanent: true).Id;
        int functorFoo1 = FunctorTable.Intern(atomFoo, 1);
        int functorBar1 = FunctorTable.Intern(atomBar, 1);

        // Build bar(x) at X[1], then foo(<ref-to-bar>) at X[0]:
        //   put_structure bar/1, X[1]
        //   unify_atom 'x'
        //   put_structure foo/1, X[0]
        //   unify_value_x X[1]      ; foo's arg = X[1] (REF to bar(x))
        //   call <clause>
        //   halt
        var emitter = new BytecodeEmitter();
        emitter.EmitPutStructure(functorBar1, 1);
        emitter.EmitUnifyAtom(atomX);
        emitter.EmitPutStructure(functorFoo1, 0);
        emitter.EmitUnifyValueX(1);
        int callPos = emitter.Position;
        emitter.EmitCall(targetAddress: 0, numLivePermanents: 0);
        emitter.EmitHalt();
        int clauseStart = emitter.Position;

        byte[] prefix = emitter.ToBytes();
        byte[] full = new byte[prefix.Length + cc.Bytecode.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(cc.Bytecode, 0, full, prefix.Length, cc.Bytecode.Length);
        BytecodeIO.WriteInt32(full, callPos + 1, clauseStart);

        var engine = new Engine();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(full, 0));
    }

    [Fact]
    public void EndToEnd_ListHead_MatchesAgainstBuiltList()
    {
        // Compile 'p([a]).'  Caller builds [a] and passes it.
        var cc = CompileSource("p([a]).");
        int atomA = AtomTable.Intern("a", permanent: true).Id;

        var emitter = new BytecodeEmitter();
        emitter.EmitPutList(0);
        emitter.EmitUnifyAtom(atomA);
        emitter.EmitUnifyNil();
        int callPos = emitter.Position;
        emitter.EmitCall(targetAddress: 0, numLivePermanents: 0);
        emitter.EmitHalt();
        int clauseStart = emitter.Position;

        byte[] prefix = emitter.ToBytes();
        byte[] full = new byte[prefix.Length + cc.Bytecode.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(cc.Bytecode, 0, full, prefix.Length, cc.Bytecode.Length);
        BytecodeIO.WriteInt32(full, callPos + 1, clauseStart);

        var engine = new Engine();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(full, 0));
    }
}

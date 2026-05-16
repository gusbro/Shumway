using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

public class ClauseCompilerTests
{
    private static CompiledClause CompileSource(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        Assert.Single(clauses);
        return new ClauseCompiler().Compile(clauses[0]);
    }

    // ---------- Per-arg-kind emission ----------

    [Fact]
    public void Fact_NoArgs_EmitsOnlyProceed()
    {
        var cc = CompileSource("foo.");
        Assert.Equal(0, cc.Arity);
        Assert.Equal(new byte[] { (byte)Opcode.Proceed }, cc.Bytecode);
    }

    [Fact]
    public void Fact_SingleAtomArg_EmitsGetAtomThenProceed()
    {
        var cc = CompileSource("p(a).");
        int atomA = AtomTable.Intern("a", permanent: true).Id;

        var disasm = Disassemble(cc.Bytecode);
        Assert.Equal(2, disasm.Count);
        Assert.Equal(Opcode.GetAtom, disasm[0].Opcode);
        Assert.Equal(atomA, disasm[0].Operands[0]);
        Assert.Equal(0, disasm[0].Operands[1]);   // X[0]
        Assert.Equal(Opcode.Proceed, disasm[1].Opcode);
    }

    [Fact]
    public void Fact_IntegerArg_EmitsGetInteger()
    {
        var cc = CompileSource("p(42).");
        var disasm = Disassemble(cc.Bytecode);
        Assert.Equal(Opcode.GetInteger, disasm[0].Opcode);
        Assert.Equal(42, disasm[0].Operands[0]);
        Assert.Equal(0, disasm[0].Operands[1]);
    }

    [Fact]
    public void Fact_VariableArg_EmitsNoHeadOpcode()
    {
        var cc = CompileSource("p(X).");
        var disasm = Disassemble(cc.Bytecode);
        Assert.Single(disasm);
        Assert.Equal(Opcode.Proceed, disasm[0].Opcode);
    }

    [Fact]
    public void Fact_AnonymousVariable_EmitsNoOpcode()
    {
        var cc = CompileSource("p(_).");
        var disasm = Disassemble(cc.Bytecode);
        Assert.Single(disasm);
        Assert.Equal(Opcode.Proceed, disasm[0].Opcode);
    }

    [Fact]
    public void Fact_RepeatedVariable_EmitsGetValueXAgainstFirstSlot()
    {
        var cc = CompileSource("p(X, X).");
        var disasm = Disassemble(cc.Bytecode);
        // First X claims X[0]. Second X emits get_value_x X[0], X[1].
        Assert.Equal(2, disasm.Count);
        Assert.Equal(Opcode.GetValueX, disasm[0].Opcode);
        Assert.Equal(0, disasm[0].Operands[0]);
        Assert.Equal(1, disasm[0].Operands[1]);
        Assert.Equal(Opcode.Proceed, disasm[1].Opcode);
    }

    [Fact]
    public void Fact_MultipleArgs_EmitsInOrder()
    {
        var cc = CompileSource("p(a, 7, X).");
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        var disasm = Disassemble(cc.Bytecode);

        Assert.Equal(3, disasm.Count);
        // get_atom 'a', X[0]; get_integer 7, X[1]; proceed
        Assert.Equal(Opcode.GetAtom, disasm[0].Opcode);
        Assert.Equal(atomA, disasm[0].Operands[0]);
        Assert.Equal(0, disasm[0].Operands[1]);
        Assert.Equal(Opcode.GetInteger, disasm[1].Opcode);
        Assert.Equal(7, disasm[1].Operands[0]);
        Assert.Equal(1, disasm[1].Operands[1]);
        Assert.Equal(Opcode.Proceed, disasm[2].Opcode);
    }

    [Fact]
    public void Fact_FunctorIdMatchesAtomTableLookup()
    {
        var cc = CompileSource("test(a, b).");
        int atomTest = AtomTable.Intern("test", permanent: true).Id;
        int functorTest2 = FunctorTable.Intern(atomTest, 2);
        Assert.Equal(functorTest2, cc.FunctorId);
        Assert.Equal(2, cc.Arity);
    }

    // ---------- Rule with trivial body ----------

    [Fact]
    public void Rule_BodyIsTrue_CompilesLikeFact()
    {
        var cc = CompileSource("p(X) :- true.");
        var disasm = Disassemble(cc.Bytecode);
        Assert.Single(disasm);
        Assert.Equal(Opcode.Proceed, disasm[0].Opcode);
    }

    // ---------- Unsupported head arg types ----------

    [Fact]
    public void HeadArg_Float_EmitsGetFloat()
    {
        // Float head args go through the new get_float opcode + the module's
        // float literal pool — see ArithmeticTests for the runtime side.
        var cc = CompileSource("p(3.14).");
        var d = Disassemble(cc.Bytecode);
        Assert.Equal(Opcode.GetFloat, d[0].Opcode);
        Assert.Equal(0, d[0].Operands[1]);            // arg slot X[0]
        Assert.Equal(Opcode.Proceed, d[1].Opcode);
    }

    // ---------- End-to-end: parse → compile → run ----------

    [Fact]
    public void EndToEnd_FactWithAtom_MatchingCallSucceeds()
    {
        // Compile the source then build a tiny caller that does:
        //   put_atom 'a', X[0]
        //   call <clause>
        //   halt
        // The combined bytecode runs to halt; final X[0] is still Atom('a').
        var cc = CompileSource("p(a).");
        int atomA = AtomTable.Intern("a", permanent: true).Id;

        var emitter = new BytecodeEmitter();
        emitter.EmitPutAtom(atomA, 0);
        // The call's target is the offset right after the halt: emit a placeholder
        // call, then a halt, then append the clause's bytes.
        int callPosition = emitter.Position;
        emitter.EmitCall(targetAddress: 0, numLivePermanents: 0);  // patched below
        int haltPosition = emitter.Position;
        emitter.EmitHalt();
        int clauseStart = emitter.Position;
        byte[] prefix = emitter.ToBytes();
        byte[] full = new byte[prefix.Length + cc.Bytecode.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(cc.Bytecode, 0, full, prefix.Length, cc.Bytecode.Length);
        // Patch the call's target operand (offset clauseStart) at position callPosition+1.
        BytecodeIO.WriteInt32(full, callPosition + 1, clauseStart);

        var engine = new Engine();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(full, 0));
        Assert.Equal(Cell.Atom(atomA), engine.GetRegister(0));
    }

    [Fact]
    public void EndToEnd_FactWithAtom_NonMatchingCallFails()
    {
        var cc = CompileSource("p(a).");
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        int atomB = AtomTable.Intern("b", permanent: true).Id;

        var emitter = new BytecodeEmitter();
        emitter.EmitPutAtom(atomB, 0);   // caller passes 'b' — won't unify with 'a'
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
    public void EndToEnd_FactBindsCallersUnboundVariable()
    {
        // Compile 'p(a).'. Caller passes an unbound variable; head match should
        // bind it to atom 'a'.
        var cc = CompileSource("p(a).");
        int atomA = AtomTable.Intern("a", permanent: true).Id;

        var emitter = new BytecodeEmitter();
        emitter.EmitPutVariableX(1, 0);   // X[0] = X[1] = fresh heap unbound
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

        // X[1] derefs to Atom('a').
        Cell x1 = engine.GetRegister(1);
        Assert.Equal(Tag.Ref, x1.Tag);
        int dest = engine.Deref(x1.AsHeapIndex);
        Assert.Equal(Cell.Atom(atomA), engine.GetHeap(dest));
    }

    // ---------- Disassembly helper ----------

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
}

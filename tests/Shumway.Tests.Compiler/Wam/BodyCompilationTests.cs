using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

public class BodyCompilationTests
{
    private static CompiledClause CompileSource(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        Assert.Single(clauses);
        return new ClauseCompiler().Compile(clauses[0]);
    }

    private record struct DisasmRow(Opcode Opcode, int[] Operands, int Offset);

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
            rows.Add(new DisasmRow(op, operands, pc));
            pc += info.Size;
        }
        return rows;
    }

    // ---------- Single-goal body (no permanents needed) ----------

    [Fact]
    public void SingleGoalBody_NoVars_EmitsExecuteOnly()
    {
        // p :- q.  →  execute q/0
        var cc = CompileSource("p :- q.");
        var d = Disassemble(cc.Bytecode);

        Assert.Single(d);
        Assert.Equal(Opcode.Execute, d[0].Opcode);
        Assert.Equal(0, cc.PermanentCount);          // no allocate
        Assert.Single(cc.CallSites);
        Assert.True(cc.CallSites[0].IsExecute);
    }

    [Fact]
    public void SingleGoalBody_PassesHeadVar_EmitsNothingForVarOnly()
    {
        // p(X) :- q(X).
        // Head: X claims X[0] (no opcode).
        // Body: q(X) — X is already at X[0] (no put needed); execute q/1.
        var cc = CompileSource("p(X) :- q(X).");
        var d = Disassemble(cc.Bytecode);

        Assert.Single(d);
        Assert.Equal(Opcode.Execute, d[0].Opcode);
        Assert.Equal(0, cc.PermanentCount);
    }

    [Fact]
    public void SingleGoalBody_PassesAtom_EmitsPutAtomThenExecute()
    {
        // p :- q(foo).  →  put_atom 'foo', X[0]; execute q/1
        var cc = CompileSource("p :- q(foo).");
        var d = Disassemble(cc.Bytecode);

        Assert.Equal(2, d.Count);
        Assert.Equal(Opcode.PutAtom, d[0].Opcode);
        Assert.Equal(Opcode.Execute, d[1].Opcode);
    }

    // ---------- Permanent variable analysis ----------

    [Fact]
    public void Permanent_VarInTwoChunks_IsAllocated()
    {
        // p(X) :- q(X), r(X).  →  X appears in chunk 0 (head + q) and chunk 1 (r) — permanent.
        var cc = CompileSource("p(X) :- q(X), r(X).");
        Assert.Equal(1, cc.PermanentCount);

        var d = Disassemble(cc.Bytecode);
        // Layout:
        //   allocate 1
        //   get_variable_y 0, X[0]   ; head: Y[0] := X[0]
        //   put_value_y 0, X[0]      ; before q: X[0] = Y[0]
        //   call q/1, 1
        //   put_value_y 0, X[0]      ; before r: X[0] = Y[0]
        //   deallocate
        //   execute r/1
        Assert.Equal(Opcode.Allocate, d[0].Opcode);
        Assert.Equal(1, d[0].Operands[0]);
        Assert.Equal(Opcode.GetVariableY, d[1].Opcode);
        Assert.Equal(0, d[1].Operands[0]);
    }

    [Fact]
    public void Permanent_VarInOneChunkOnly_IsTemp()
    {
        // p(X, Y) :- q(X), r(Y).
        // X is in chunk 0 (head + q). Y is in head AND in chunk 1 (r) — permanent.
        var cc = CompileSource("p(X, Y) :- q(X), r(Y).");
        Assert.Equal(1, cc.PermanentCount);          // only Y
    }

    [Fact]
    public void NoVars_NoPermanents_NoAllocate()
    {
        var cc = CompileSource("p :- q, r.");
        Assert.Equal(0, cc.PermanentCount);
    }

    // ---------- Multi-goal bodies ----------

    [Fact]
    public void TwoGoalBody_EmitsAllocateCallDeallocateExecute()
    {
        var cc = CompileSource("p :- q, r.");
        var d = Disassemble(cc.Bytecode);

        // p :- q, r.  → allocate 0; call q/0, 0; deallocate; execute r/0
        // The frame is needed for CP preservation even though no permanents exist
        // (otherwise the last goal's execute would loop back into p's own body).
        Assert.Equal(4, d.Count);
        Assert.Equal(Opcode.Allocate, d[0].Opcode);
        Assert.Equal(0, d[0].Operands[0]);
        Assert.Equal(Opcode.Call, d[1].Opcode);
        Assert.Equal(Opcode.Deallocate, d[2].Opcode);
        Assert.Equal(Opcode.Execute, d[3].Opcode);

        Assert.Equal(2, cc.CallSites.Count);
        Assert.False(cc.CallSites[0].IsExecute);
        Assert.True(cc.CallSites[1].IsExecute);
    }

    [Fact]
    public void MultiGoalBody_WithPermanent_HasAllocateAndDeallocate()
    {
        var cc = CompileSource("p(X) :- q(X), r(X).");
        var d = Disassemble(cc.Bytecode);

        // First instruction is allocate, last before execute is deallocate.
        Assert.Equal(Opcode.Allocate, d[0].Opcode);
        Assert.Equal(Opcode.Execute, d[^1].Opcode);
        Assert.Equal(Opcode.Deallocate, d[^2].Opcode);
    }

    // ---------- Anonymous variables in body ----------

    [Fact]
    public void Body_AnonymousVar_EmitsPutVariableX()
    {
        // p :- q(_).  →  put_variable_x N, 0; execute q/1
        var cc = CompileSource("p :- q(_).");
        var d = Disassemble(cc.Bytecode);

        Assert.Equal(Opcode.PutVariableX, d[0].Opcode);
        Assert.Equal(Opcode.Execute, d[1].Opcode);
    }

    // ---------- Compound body args ----------

    [Fact]
    public void Body_CompoundArg_EmitsPutStructure()
    {
        // p :- q(foo(a)).
        var cc = CompileSource("p :- q(foo(a)).");
        var d = Disassemble(cc.Bytecode);

        Assert.Equal(Opcode.PutStructure, d[0].Opcode);
        Assert.Equal(Opcode.UnifyAtom, d[1].Opcode);
        Assert.Equal(Opcode.Execute, d[2].Opcode);
    }

    [Fact]
    public void Body_ListArg_EmitsPutListAndUnifies()
    {
        // p :- q([a]).
        var cc = CompileSource("p :- q([a]).");
        var d = Disassemble(cc.Bytecode);

        Assert.Equal(Opcode.PutList, d[0].Opcode);
        Assert.Equal(Opcode.UnifyAtom, d[1].Opcode);
        Assert.Equal(Opcode.UnifyNil, d[2].Opcode);
        Assert.Equal(Opcode.Execute, d[3].Opcode);
    }

    // ---------- End-to-end: parse → compile → link → run ----------

    private static (byte[] bytecode, Dictionary<int, int> functorToAddress) LinkProgram(params CompiledClause[] clauses)
    {
        // Concatenate all clauses' bytecode and remember each clause's starting address.
        var all = new List<byte>();
        var addresses = new Dictionary<int, int>();
        foreach (var c in clauses)
        {
            addresses[c.FunctorId] = all.Count;
            all.AddRange(c.Bytecode);
        }
        byte[] program = all.ToArray();

        // Patch every call site so its target operand points at the callee's address.
        int clauseStart = 0;
        foreach (var c in clauses)
        {
            foreach (var site in c.CallSites)
            {
                if (!addresses.TryGetValue(site.CalleeFunctorId, out int target))
                    throw new InvalidOperationException(
                        $"Call to functor id {site.CalleeFunctorId} cannot be resolved.");
                BytecodeIO.WriteInt32(program, clauseStart + site.OpcodeOffset + 1, target);
            }
            clauseStart += c.Bytecode.Length;
        }
        return (program, addresses);
    }

    [Fact]
    public void EndToEnd_SingleGoalBody_RoundTripsThroughCallee()
    {
        // Program:  p :- q.   q.
        // Top-level caller does call p; q is a fact.
        var p = CompileSource("p :- q.");
        var q = CompileSource("q.");
        var (program, addrs) = LinkProgram(p, q);

        // Prepend a top-level launcher: call p; halt.
        var launcher = new BytecodeEmitter();
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        byte[] prefix = launcher.ToBytes();

        byte[] full = new byte[prefix.Length + program.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(program, 0, full, prefix.Length, program.Length);
        BytecodeIO.WriteInt32(full, callPos + 1, prefix.Length + addrs[p.FunctorId]);
        foreach (var c in new[] { p, q })
        {
            // Shift each clause's call-site patches by prefix length (already
            // applied within LinkProgram against addresses 0..; re-do here.).
        }
        // Re-patch p's call site to point past prefix.
        BytecodeIO.WriteInt32(full, prefix.Length + p.CallSites[0].OpcodeOffset + 1,
            prefix.Length + addrs[q.FunctorId]);

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(full, 0));
    }

    [Fact]
    public void EndToEnd_TwoGoalBody_BothGoalsExecute()
    {
        // p :- q, r.    q.    r.
        var p = CompileSource("p :- q, r.");
        var q = CompileSource("q.");
        var r = CompileSource("r.");

        var (program, addrs) = LinkProgram(p, q, r);

        var launcher = new BytecodeEmitter();
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        byte[] prefix = launcher.ToBytes();

        byte[] full = new byte[prefix.Length + program.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(program, 0, full, prefix.Length, program.Length);
        BytecodeIO.WriteInt32(full, callPos + 1, prefix.Length + addrs[p.FunctorId]);
        // p's call sites point at q and r (in order). Patch them with the shifted addresses.
        foreach (var site in p.CallSites)
            BytecodeIO.WriteInt32(full, prefix.Length + site.OpcodeOffset + 1,
                prefix.Length + addrs[site.CalleeFunctorId]);

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(full, 0));
    }

    [Fact]
    public void EndToEnd_PermanentVar_SurvivesCall()
    {
        // p(X) :- q(X), r(X).      q(_).      r(a).
        // Caller passes atom 'a' as X. q accepts anything (succeeds). Then X
        // (= 'a') must survive q's call and arrive at r intact.
        var p = CompileSource("p(X) :- q(X), r(X).");
        var q = CompileSource("q(_).");
        var r = CompileSource("r(a).");

        // Sanity: p has one permanent (X).
        Assert.Equal(1, p.PermanentCount);

        var (program, addrs) = LinkProgram(p, q, r);

        // Launcher: put_atom 'a', X[0]; call p/1; halt.
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        var launcher = new BytecodeEmitter();
        launcher.EmitPutAtom(atomA, 0);
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        byte[] prefix = launcher.ToBytes();

        byte[] full = new byte[prefix.Length + program.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(program, 0, full, prefix.Length, program.Length);
        BytecodeIO.WriteInt32(full, callPos + 1, prefix.Length + addrs[p.FunctorId]);
        foreach (var site in p.CallSites)
            BytecodeIO.WriteInt32(full, prefix.Length + site.OpcodeOffset + 1,
                prefix.Length + addrs[site.CalleeFunctorId]);

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Halted, interp.Run(full, 0));
    }

    [Fact]
    public void EndToEnd_PermanentVar_MismatchInLastGoalFails()
    {
        // Same program as above, but caller passes 'b'. r(a) won't unify.
        var p = CompileSource("p(X) :- q(X), r(X).");
        var q = CompileSource("q(_).");
        var r = CompileSource("r(a).");
        var (program, addrs) = LinkProgram(p, q, r);

        int atomB = AtomTable.Intern("b", permanent: true).Id;
        var launcher = new BytecodeEmitter();
        launcher.EmitPutAtom(atomB, 0);
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        byte[] prefix = launcher.ToBytes();

        byte[] full = new byte[prefix.Length + program.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(program, 0, full, prefix.Length, program.Length);
        BytecodeIO.WriteInt32(full, callPos + 1, prefix.Length + addrs[p.FunctorId]);
        foreach (var site in p.CallSites)
            BytecodeIO.WriteInt32(full, prefix.Length + site.OpcodeOffset + 1,
                prefix.Length + addrs[site.CalleeFunctorId]);

        var engine = new Activation();
        var interp = new BytecodeInterpreter(engine);
        Assert.Equal(InterpreterResult.Failed, interp.Run(full, 0));
    }
}

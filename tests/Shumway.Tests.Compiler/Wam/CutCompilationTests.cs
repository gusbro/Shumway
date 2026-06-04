using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

public class CutCompilationTests
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

    // ---------- Neck cut (! is the first goal) ----------

    [Fact]
    public void NeckCut_SingleGoalBody_EmitsNeckCutThenProceed()
    {
        // p :- !.   → neck_cut; proceed
        var cc = CompileSource("p :- !.");
        var d = Disassemble(cc.Bytecode);

        Assert.Equal(2, d.Count);
        Assert.Equal(Opcode.NeckCut, d[0].Opcode);
        Assert.Equal(Opcode.Proceed, d[1].Opcode);
        Assert.Equal(0, cc.PermanentCount);                  // no frame needed
    }

    [Fact]
    public void NeckCut_FollowedByGoal_NeedsNoFrame()
    {
        // p :- !, q.   → neck_cut; execute q.  No permanents, no deep cut, and a
        // single tail call after a transparent neck cut → no environment frame
        // (Phase 26 B; matches GProlog, which emits no allocate here).
        var cc = CompileSource("p :- !, q.");
        var d = Disassemble(cc.Bytecode);

        Assert.Equal(Opcode.NeckCut, d[0].Opcode);
        Assert.Equal(Opcode.Execute, d[1].Opcode);
        Assert.DoesNotContain(d, i => i.Opcode == Opcode.Allocate);
    }

    // ---------- Deep cut (! at position > 0) ----------

    [Fact]
    public void DeepCut_LastGoal_EmitsGetLevelAndCut()
    {
        // p :- q, !.  Chunk 220 fuses the Allocate+GetLevel prologue and
        // the Deallocate+Proceed epilogue:
        //   allocate_get_level 1, Y[0] ; Nop ; call q ; cut Y[0] ; deallocate_proceed ; Nop
        var cc = CompileSource("p :- q, !.");
        var d = Disassemble(cc.Bytecode);

        // The Disassembler advances by the fused opcode's full width;
        // the Nop padding lives inside those bytes, not as a separate
        // instruction entry.
        Assert.Equal(Opcode.AllocateGetLevel, d[0].Opcode);
        Assert.Equal(1, d[0].Operands[0]);                   // count = 1 (cut slot)
        Assert.Equal(0, d[0].Operands[1]);                   // slot = Y[0]
        Assert.Equal(Opcode.Call, d[1].Opcode);
        Assert.Equal(Opcode.Cut, d[2].Opcode);
        Assert.Equal(0, d[2].Operands[0]);                   // Y[0]
        Assert.Equal(Opcode.DeallocateProceed, d[3].Opcode);
    }

    [Fact]
    public void DeepCut_MiddleGoal_EmitsGetLevelCallCutCallExecute()
    {
        // p :- q, !, r.   Chunk 220 fuses Allocate+GetLevel prologue but
        // Deallocate+Execute stays unfused (only Deallocate+Proceed is fused).
        var cc = CompileSource("p :- q, !, r.");
        var d = Disassemble(cc.Bytecode);

        Assert.Equal(Opcode.AllocateGetLevel, d[0].Opcode);
        Assert.Equal(Opcode.Call, d[1].Opcode);
        Assert.Equal(Opcode.Cut, d[2].Opcode);
        Assert.Equal(Opcode.Deallocate, d[3].Opcode);
        Assert.Equal(Opcode.Execute, d[4].Opcode);
    }

    [Fact]
    public void DeepCut_WithPermanentVariable_AllocatesBothSlots()
    {
        // p(X) :- q(X), !, r(X).
        // X is permanent (chunks 0 and 2). With cut, need 2 slots: Y[0] for X, Y[1] for cut.
        var cc = CompileSource("p(X) :- q(X), !, r(X).");
        Assert.Equal(2, cc.PermanentCount);

        var d = Disassemble(cc.Bytecode);
        Assert.Equal(Opcode.AllocateGetLevel, d[0].Opcode);  // chunk 220 fused prologue
        Assert.Equal(2, d[0].Operands[0]);                   // count = 2 (X + cut)
    }

    [Fact]
    public void MultipleDeepCuts_OnlyOneCutSlot()
    {
        // p :- q, !, r, !.   — multiple `!` deeper than position 0. They all
        // read the same cut slot (the first `!` discards the CPs; subsequent
        // ones are no-ops but the compiler still emits them).
        var cc = CompileSource("p :- q, !, r, !.");
        Assert.Equal(1, cc.PermanentCount);                  // one cut slot

        var d = Disassemble(cc.Bytecode);
        int firstCut = d.FindIndex(r => r.Opcode == Opcode.Cut);
        int secondCut = d.FindIndex(firstCut + 1, r => r.Opcode == Opcode.Cut);
        Assert.True(firstCut > 0);
        Assert.True(secondCut > firstCut);
        Assert.Equal(d[firstCut].Operands[0], d[secondCut].Operands[0]);   // same Y slot
    }

    // ---------- End-to-end ----------

    private record struct AssembledProgram(byte[] Bytecode, IReadOnlyList<SwitchTable> SwitchTables);

    private static AssembledProgram AssembleProgram(
        CompiledModule module,
        int entryFunctorId,
        Action<BytecodeEmitter> setupArgs)
    {
        var launcher = new BytecodeEmitter();
        setupArgs(launcher);
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        byte[] prefix = launcher.ToBytes();

        var linkResult = new Linker().Link(module, loadOffset: prefix.Length);
        BytecodeIO.WriteInt32(prefix, callPos + 1, linkResult.Addresses[entryFunctorId]);

        byte[] full = new byte[prefix.Length + linkResult.Bytecode.Length];
        Array.Copy(prefix, full, prefix.Length);
        Array.Copy(linkResult.Bytecode, 0, full, prefix.Length, linkResult.Bytecode.Length);
        return new AssembledProgram(full, linkResult.SwitchTables);
    }

    [Fact]
    public void EndToEnd_NeckCut_DiscardsTryMeElseCp()
    {
        // p(a) :- !.    p(b).
        // ?- p(a). → Halted, and after halt the predicate's CP from try_me_else
        // has been discarded (engine.B is -1 because the cut committed).
        var module = new ModuleCompiler().Compile(
            new ClauseReader("p(a) :- !.\np(b).\n").ReadAll());
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        int pFunctor = FunctorTable.Intern(
            AtomTable.Intern("p", permanent: true).Id, 1);

        var program = AssembleProgram(module, pFunctor,
            launcher => launcher.EmitPutAtom(atomA, 0));

        var engine = new Engine();
        var interp = new BytecodeInterpreter(
            engine, Array.Empty<string>(), Array.Empty<double>(), program.SwitchTables);
        Assert.Equal(InterpreterResult.Halted, interp.Run(program.Bytecode, 0));
        Assert.Equal(-1, engine.B);                           // try_me_else CP was cut away
    }

    [Fact]
    public void EndToEnd_DeepCut_DiscardsSubgoalCp()
    {
        // p(X) :- q(X), !.       q(a). q(b).
        //
        // ?- p(a). q has two clauses so its try_me_else pushes a CP. q(a)
        // matches the first. Then `!` runs — Y[0] holds p's entry _b0 = -1
        // (the launcher had no CPs), so the cut to -1 discards q's CP.
        var module = new ModuleCompiler().Compile(
            new ClauseReader("p(X) :- q(X), !.\nq(a).\nq(b).\n").ReadAll());
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        int pFunctor = FunctorTable.Intern(
            AtomTable.Intern("p", permanent: true).Id, 1);

        var program = AssembleProgram(module, pFunctor,
            launcher => launcher.EmitPutAtom(atomA, 0));

        var engine = new Engine();
        var interp = new BytecodeInterpreter(
            engine, Array.Empty<string>(), Array.Empty<double>(), program.SwitchTables);
        Assert.Equal(InterpreterResult.Halted, interp.Run(program.Bytecode, 0));
        Assert.Equal(-1, engine.B);                           // q's CP was cut away
    }

    [Fact]
    public void EndToEnd_CutPreventsBacktrackingIntoAlternativeOnFailure()
    {
        // p(a) :- !, q(b).     % cut commits to clause 1, then q(b) fails
        // p(a).                % alternative — would succeed if reached
        // q(a).                % q(b) won't match
        //
        // ?- p(a). With cut, the failure of q(b) does NOT retry p's clause 2
        // because the cut already discarded p's try_me_else CP. Final result
        // is Failed.
        var module = new ModuleCompiler().Compile(
            new ClauseReader("p(a) :- !, q(b).\np(a).\nq(a).\n").ReadAll());
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        int pFunctor = FunctorTable.Intern(
            AtomTable.Intern("p", permanent: true).Id, 1);

        var program = AssembleProgram(module, pFunctor,
            launcher => launcher.EmitPutAtom(atomA, 0));

        var engine = new Engine();
        var interp = new BytecodeInterpreter(
            engine, Array.Empty<string>(), Array.Empty<double>(), program.SwitchTables);
        Assert.Equal(InterpreterResult.Failed, interp.Run(program.Bytecode, 0));
    }

    [Fact]
    public void EndToEnd_WithoutCut_BacktrackingDoesReachAlternative()
    {
        // Same shape but without the cut — clause 1 still fails on q(b),
        // but now backtracking IS allowed, so clause 2 succeeds.
        var module = new ModuleCompiler().Compile(
            new ClauseReader("p(a) :- q(b).\np(a).\nq(a).\n").ReadAll());
        int atomA = AtomTable.Intern("a", permanent: true).Id;
        int pFunctor = FunctorTable.Intern(
            AtomTable.Intern("p", permanent: true).Id, 1);

        var program = AssembleProgram(module, pFunctor,
            launcher => launcher.EmitPutAtom(atomA, 0));

        var engine = new Engine();
        var interp = new BytecodeInterpreter(
            engine, Array.Empty<string>(), Array.Empty<double>(), program.SwitchTables);
        Assert.Equal(InterpreterResult.Halted, interp.Run(program.Bytecode, 0));
    }
}

using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

public class PredicateCompilerTests
{
    private static CompiledPredicate CompilePredicate(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        return new PredicateCompiler().Compile(clauses);
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

    // ---------- Single-clause: no wrapping ----------

    [Fact]
    public void SingleClause_HasNoChoicePointDispatch()
    {
        // p(a).  →  get_atom 'a', X[0]; proceed   (no try/trust)
        var cp = CompilePredicate("p(a).");
        Assert.Equal(1, cp.ClauseCount);
        var d = Disassemble(cp.Bytecode);
        Assert.DoesNotContain(d, r => r.Opcode == Opcode.TryMeElse);
        Assert.DoesNotContain(d, r => r.Opcode == Opcode.RetryMeElse);
        Assert.DoesNotContain(d, r => r.Opcode == Opcode.TrustMe);
    }

    // ---------- Two-clause: try_me_else + trust_me ----------

    [Fact]
    public void TwoClauses_VarArgs_EmitsTryAndTrust()
    {
        // Variable-headed clauses don't trigger first-arg indexing, so we
        // stay on the classical try_me_else / trust_me chain.
        var cp = CompilePredicate("p(X) :- q(X).\np(Y) :- r(Y).\n");
        Assert.Equal(2, cp.ClauseCount);

        var d = Disassemble(cp.Bytecode);
        Assert.Equal(Opcode.TryMeElse, d[0].Opcode);
        Assert.Equal(1, d[0].Operands[1]);   // arity
        // BP of try_me_else must point at the trust_me opcode start.
        int trustMeIdx = d.FindIndex(r => r.Opcode == Opcode.TrustMe);
        Assert.True(trustMeIdx > 0);
        Assert.Equal(d[trustMeIdx].Offset, d[0].Operands[0]);
    }

    // ---------- Three-clause: try + retry + trust ----------

    [Fact]
    public void ThreeClauses_VarArgs_EmitsTryRetryTrust()
    {
        // Var args again — the indexing-free path under
        // try_me_else / retry_me_else / trust_me.
        var cp = CompilePredicate("p(X) :- q(X).\np(Y) :- r(Y).\np(Z) :- s(Z).\n");
        Assert.Equal(3, cp.ClauseCount);

        var d = Disassemble(cp.Bytecode);
        Assert.Equal(Opcode.TryMeElse, d[0].Opcode);

        int retryIdx = d.FindIndex(r => r.Opcode == Opcode.RetryMeElse);
        int trustIdx = d.FindIndex(r => r.Opcode == Opcode.TrustMe);
        Assert.True(retryIdx > 0);
        Assert.True(trustIdx > retryIdx);

        // try_me_else's BP points at retry_me_else.
        Assert.Equal(d[retryIdx].Offset, d[0].Operands[0]);
        // retry_me_else's BP points at trust_me.
        Assert.Equal(d[trustIdx].Offset, d[retryIdx].Operands[0]);
    }

    [Fact]
    public void TwoClauses_AtomArgs_EmitsSwitchOnTerm()
    {
        // p(a). p(b). — both atom first args, so the indexing path runs and
        // we see switch_on_term + a switch_on_atom inside ConstLbl.
        var cp = CompilePredicate("p(a).\np(b).\n");
        var d = Disassemble(cp.Bytecode);
        Assert.Equal(Opcode.SwitchOnTerm, d[0].Opcode);
        Assert.Contains(d, r => r.Opcode == Opcode.SwitchOnAtom);
    }

    // ---------- Per-clause CallSites get shifted ----------

    [Fact]
    public void TwoClauses_CallSitesShiftedToPredicateLocalOffsets()
    {
        // Two-clause predicate, each clause has one execute. Verify both
        // CallSites have offsets inside the predicate bytecode and point at
        // the correct opcode.
        var cp = CompilePredicate("p(X) :- q(X).\np(X) :- r(X).\n");
        Assert.Equal(2, cp.ClauseCount);

        var d = Disassemble(cp.Bytecode);
        var execs = d.Where(r => r.Opcode == Opcode.Execute).ToList();
        Assert.Equal(2, execs.Count);

        Assert.Equal(2, cp.CallSites.Count);
        Assert.Equal(execs[0].Offset, cp.CallSites[0].OpcodeOffset);
        Assert.Equal(execs[1].Offset, cp.CallSites[1].OpcodeOffset);
    }

    // ---------- Mismatched functors throw ----------

    [Fact]
    public void DifferentFunctors_Throws()
    {
        // PredicateCompiler shouldn't be handed a list with mixed functors.
        var clauses = new ClauseReader("p(a).\nq(b).\n").ReadAll().ToList();
        Assert.Throws<ArgumentException>(() => new PredicateCompiler().Compile(clauses));
    }

    [Fact]
    public void EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PredicateCompiler().Compile(Array.Empty<Shumway.Compiler.Ast.Clause>()));
    }

    // ---------- End-to-end: multi-clause predicate ----------

    /// <summary>Helper for end-to-end runs: builds a launcher with the given
    /// argument setup, links the module so all internal references already
    /// account for the launcher's length, then concatenates the two.</summary>
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
    public void EndToEnd_MultiClausePredicate_MatchesByBacktracking()
    {
        // Module:    p(a). p(b). p(c).
        // Caller:    put_atom 'b', X[0]; call p/1; halt
        // Expected:  Halted (clause 2 matches after one retry).
        var module = new ModuleCompiler().Compile(
            new ClauseReader("p(a).\np(b).\np(c).\n").ReadAll());
        Assert.Single(module.Predicates);
        Assert.Equal(3, module.Predicates[0].ClauseCount);

        int atomB = AtomTable.Intern("b", permanent: true).Id;
        int pFunctor = FunctorTable.Intern(
            AtomTable.Intern("p", permanent: true).Id, 1);

        var program = AssembleProgram(module, pFunctor,
            launcher => launcher.EmitPutAtom(atomB, 0));

        var engine = new Activation();
        var interp = new BytecodeInterpreter(
            engine, Array.Empty<string>(), Array.Empty<double>(), program.SwitchTables);
        Assert.Equal(InterpreterResult.Halted, interp.Run(program.Bytecode, 0));
    }

    [Fact]
    public void EndToEnd_MultiClause_AllFailExhaustsAndReturnsFailed()
    {
        // Same predicate, but caller passes an atom that matches none of the clauses.
        var module = new ModuleCompiler().Compile(
            new ClauseReader("p(a).\np(b).\np(c).\n").ReadAll());

        int atomD = AtomTable.Intern("d", permanent: true).Id;
        int pFunctor = FunctorTable.Intern(
            AtomTable.Intern("p", permanent: true).Id, 1);

        var program = AssembleProgram(module, pFunctor,
            launcher => launcher.EmitPutAtom(atomD, 0));

        var engine = new Activation();
        var interp = new BytecodeInterpreter(
            engine, Array.Empty<string>(), Array.Empty<double>(), program.SwitchTables);
        Assert.Equal(InterpreterResult.Failed, interp.Run(program.Bytecode, 0));
    }

    // ---------- End-to-end: multi-predicate module with cross-calls ----------

    [Fact]
    public void EndToEnd_TwoPredicatesWithCrossCall()
    {
        // p :- q(a).        % calls q with 'a'
        // q(a).
        // q(b).
        //
        // ?- p.    →  Halted (p calls q(a), q's first clause matches).
        var module = new ModuleCompiler().Compile(
            new ClauseReader("p :- q(a).\nq(a).\nq(b).\n").ReadAll());
        Assert.Equal(2, module.Predicates.Count);

        int pFunctor = FunctorTable.Intern(
            AtomTable.Intern("p", permanent: true).Id, 0);

        var program = AssembleProgram(module, pFunctor, _ => { });

        var engine = new Activation();
        var interp = new BytecodeInterpreter(
            engine, Array.Empty<string>(), Array.Empty<double>(), program.SwitchTables);
        Assert.Equal(InterpreterResult.Halted, interp.Run(program.Bytecode, 0));
    }

    // ---------- ModuleCompiler: clause grouping ----------

    [Fact]
    public void ModuleCompiler_GroupsClausesByFunctor()
    {
        var module = new ModuleCompiler().Compile(
            new ClauseReader("p(a).\nq(x).\np(b).\nq(y).\n").ReadAll());

        // Two predicates: p/1 (2 clauses) and q/1 (2 clauses).
        Assert.Equal(2, module.Predicates.Count);
        Assert.All(module.Predicates, p => Assert.Equal(2, p.ClauseCount));
    }

    [Fact]
    public void ModuleCompiler_PreservesFirstOccurrenceOrder()
    {
        // q appears before p in the source despite p's clauses being interleaved.
        var module = new ModuleCompiler().Compile(
            new ClauseReader("q(x).\np(a).\nq(y).\np(b).\n").ReadAll());

        int qFunctor = FunctorTable.Intern(
            AtomTable.Intern("q", permanent: true).Id, 1);
        Assert.Equal(qFunctor, module.Predicates[0].FunctorId);
    }

    [Fact]
    public void ModuleCompiler_IgnoresDirectives()
    {
        // The directive :- dynamic foo/2 is processed by ClauseReader but
        // should not produce a predicate in the module.
        var module = new ModuleCompiler().Compile(
            new ClauseReader(":- dynamic foo/2.\nfoo(a, 1).\nfoo(b, 2).\n").ReadAll());
        Assert.Single(module.Predicates);
        Assert.Equal(2, module.Predicates[0].ClauseCount);
    }
}

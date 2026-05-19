using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// Chunk 65: bytecode-level pinning for the Warren argument scheduler.
/// Correctness lives in <see cref="Shumway.Tests.Embedding.Chunk65Tests"/>;
/// these tests count the head-var save instructions to guard against a
/// regression to the conservative one-pass preserve. Each test compiles
/// a clause that, under the old pass, would emit more
/// <c>put_value_x</c> saves than the cycle / self-loop / forced-save
/// minimum.
/// </summary>
public class WarrenSchedulerTests
{
    private static CompiledClause CompileSource(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        Assert.Single(clauses);
        return new ClauseCompiler().Compile(clauses[0]);
    }

    /// <summary>Counts <c>put_value_x</c> opcodes whose source register
    /// is in <c>[0, arity)</c> and whose destination is past the body's
    /// arg range — i.e. head-var → safe-slot moves that the scheduler
    /// emits as saves.</summary>
    private static int CountHeadVarSaves(byte[] code, int arity, int firstGoalArity)
    {
        int count = 0;
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            var info = OpcodeTable.Get(op);
            if (op == Opcode.PutValueX)
            {
                int src = BytecodeIO.ReadInt32(code, pc + 1);
                int dst = BytecodeIO.ReadInt32(code, pc + 5);
                if (src < arity && dst >= firstGoalArity) count++;
            }
            pc += info.Size;
        }
        return count;
    }

    [Fact]
    public void TwoCycle_OneSave()
    {
        // foo(X, Y) :- bar(Y, X). 2-cycle. Warren breaks with one save
        // (the minimum). Conservative pass would emit one save too —
        // already covered by chunk 62's "flat read at position > home"
        // rule — but pinning helps catch regressions.
        var cc = CompileSource("foo(X, Y) :- bar(Y, X).");
        Assert.Equal(1, CountHeadVarSaves(cc.Bytecode, arity: 2, firstGoalArity: 2));
    }

    [Fact]
    public void ThreeCycle_OneSave()
    {
        // foo(X, Y, Z) :- bar(Z, X, Y). 3-cycle. Warren breaks with one
        // save; the conservative pass would emit two (X and Y both
        // trigger "i > home").
        var cc = CompileSource("foo(X, Y, Z) :- bar(Z, X, Y).");
        Assert.Equal(1, CountHeadVarSaves(cc.Bytecode, arity: 3, firstGoalArity: 3));
    }

    [Fact]
    public void FourCycle_OneSave()
    {
        // foo(W, X, Y, Z) :- bar(Z, W, X, Y). 4-cycle. Warren breaks
        // with one save; conservative would emit three.
        var cc = CompileSource("foo(W, X, Y, Z) :- bar(Z, W, X, Y).");
        Assert.Equal(1, CountHeadVarSaves(cc.Bytecode, arity: 4, firstGoalArity: 4));
    }

    [Fact]
    public void CompoundCrossDep_OneSave()
    {
        // foo(X, Y) :- bar([Y], X). Cross-2-cycle through a compound.
        // Warren breaks with one save (of X). Conservative pass emits
        // two: X (flat read at position > home) and Y (compound
        // contains Y, conservative rule).
        var cc = CompileSource("foo(X, Y) :- bar([Y], X).");
        Assert.Equal(1, CountHeadVarSaves(cc.Bytecode, arity: 2, firstGoalArity: 2));
    }

    [Fact]
    public void NoConflict_NoSaves()
    {
        // foo(X, Y) :- bar(X, Y). Both vars at their homes — no
        // emission at all, no saves.
        var cc = CompileSource("foo(X, Y) :- bar(X, Y).");
        Assert.Equal(0, CountHeadVarSaves(cc.Bytecode, arity: 2, firstGoalArity: 2));
    }

    [Fact]
    public void ReadBeforeWrite_NoSave()
    {
        // foo(X) :- bar(X, X). Arg 0 is a no-op (X.home=0=dst); arg 1
        // reads X[0] which the no-op didn't clobber. Warren emits zero
        // saves.
        var cc = CompileSource("foo(X) :- bar(X, X).");
        Assert.Equal(0, CountHeadVarSaves(cc.Bytecode, arity: 1, firstGoalArity: 2));
    }

    [Fact]
    public void SelfLoop_OneSave()
    {
        // foo(X) :- bar([X], X). Arg 0 = [X] at dst 0 self-loops
        // (put_list 0 clobbers X[0] before unify_value_x). Plus arg 1
        // reads X[0]. Warren saves X once.
        var cc = CompileSource("foo(X) :- bar([X], X).");
        Assert.Equal(1, CountHeadVarSaves(cc.Bytecode, arity: 1, firstGoalArity: 2));
    }

    [Fact]
    public void NestedCompoundForcedSave_OneSave()
    {
        // foo(X) :- bar([[X]], 7). Depth-2 var read at drain time.
        // Forced save lifts X to a safe slot.
        var cc = CompileSource("foo(X) :- bar([[X]], 7).");
        Assert.Equal(1, CountHeadVarSaves(cc.Bytecode, arity: 1, firstGoalArity: 2));
    }
}

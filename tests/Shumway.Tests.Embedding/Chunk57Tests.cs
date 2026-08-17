using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 57: env-trimming infrastructure. The compile-time analysis
/// (live-perms-after-each-goal) emits accurate <c>num_live_perms</c>
/// operands in <c>Call</c> and <c>CallBuiltin</c> instructions. The
/// runtime <see cref="Activation.TrimEnv"/> is currently a no-op pending
/// a focused soundness review of its interaction with Tier-1 IL
/// promotion (see the method's XML doc on Activation.cs); these tests
/// pin the static analysis so the operand stays meaningful for when
/// the runtime gate flips on.
///
/// <para>Warren's argument-shuffling optimisation stays deferred —
/// the minimum-correctness <see cref="PreserveClobberedHeadVars"/>
/// pass still emits one extra <c>put_value_x</c> per clobbered head
/// var. That's a constant-factor inefficiency, not a correctness
/// gap, so it can land as a focused chunk later without unblocking
/// any Phase-1 deliverable.</para>
/// </summary>
public class Chunk57Tests
{
    [Fact]
    public void LivePerms_StaysAtLiveCount_AcrossIntermediateCalls()
    {
        // foo(X, Y) :- a(Y), b, c(X).
        //   - Chunk analysis: head + first goal = chunk 0; b = chunk 1;
        //     c = chunk 2.
        //   - Y appears in head (chunk 0) + a (chunk 0) = 1 chunk → not perm.
        //   - X appears in head (chunk 0) + c (chunk 2) = 2 chunks → perm Y[0].
        //   - After a(Y), X still live (c reads it later): liveAfter[0] = 1.
        //   - After b, X still live: liveAfter[1] = 1.
        //   - c is the last call → Execute (no operand).
        var src =
            ":- public foo/2.\n" +
            ":- public a/1.\n" +
            ":- public b/0.\n" +
            ":- public c/1.\n" +
            "foo(X, Y) :- a(Y), b, c(X).\n" +
            "a(_).\n" +
            "b.\n" +
            "c(_).\n";
        var clauses = new Shumway.Compiler.Parsing.ClauseReader(
            new Shumway.Compiler.Lexer.Lexer(src),
            Shumway.Compiler.Parsing.OperatorTable.Default()).ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);
        var foo = module.Predicates.First(
            p => AtomTable.GetById(FunctorTable.Lookup(p.FunctorId).AtomId)?.Name == "foo");
        var liveCounts = ExtractCallLivePerms(foo.Bytecode);
        Assert.Equal(2, liveCounts.Count);
        Assert.All(liveCounts, n => Assert.Equal(1, n));
    }

    [Fact]
    public void LivePerms_DropsAfterLastUse_DeepCutSlotDeadAfterCut()
    {
        // foo(X) :- p, !, q, r(X).
        //   - X perm (Y[0]); cutSlot = Y[1].
        //   - liveAfter:
        //       p: cut at position 1 needs Y[1]; X is needed later by r. → 2.
        //       q: ! is past, cutSlot dead; X live for r. → 1.
        //     (r is the last call → Execute.)
        var src =
            ":- public foo/1.\n" +
            ":- public p/0.\n" +
            ":- public q/0.\n" +
            ":- public r/1.\n" +
            "foo(X) :- p, !, q, r(X).\n" +
            "p.\n" +
            "q.\n" +
            "r(_).\n";
        var clauses = new Shumway.Compiler.Parsing.ClauseReader(
            new Shumway.Compiler.Lexer.Lexer(src),
            Shumway.Compiler.Parsing.OperatorTable.Default()).ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);
        var foo = module.Predicates.First(
            p => AtomTable.GetById(FunctorTable.Lookup(p.FunctorId).AtomId)?.Name == "foo");
        var liveCounts = ExtractCallLivePerms(foo.Bytecode);
        Assert.Equal(2, liveCounts.Count);
        Assert.Equal(2, liveCounts[0]);   // pre-cut: X + cutSlot both live
        Assert.Equal(1, liveCounts[1]);   // post-cut: only X
    }

    [Fact]
    public void LivePerms_ZeroAfterLastCall()
    {
        // bar(X) :- p(X), q.   q is last → emitted as Execute (no operand).
        //                       After p(X), nothing reads X again, so
        //                       liveAfter[0] = 0.
        var src =
            ":- public bar/1.\n" +
            ":- public p/1.\n" +
            ":- public q/0.\n" +
            "bar(X) :- p(X), q.\n" +
            "p(_).\n" +
            "q.\n";
        var clauses = new Shumway.Compiler.Parsing.ClauseReader(
            new Shumway.Compiler.Lexer.Lexer(src),
            Shumway.Compiler.Parsing.OperatorTable.Default()).ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);
        var bar = module.Predicates.First(
            p => AtomTable.GetById(FunctorTable.Lookup(p.FunctorId).AtomId)?.Name == "bar");
        var liveCounts = ExtractCallLivePerms(bar.Bytecode);
        // X is permanent (head + p), but the only body call before
        // execute(q) is p itself. After p, no perm is needed. q is the
        // last call so it's Execute (no operand counted).
        Assert.Single(liveCounts);
        Assert.Equal(0, liveCounts[0]);
    }

    [Fact]
    public void LivePerms_RuntimeTrim_IsCurrentlyNoOp()
    {
        // Sanity check: with the runtime gate off, queries still
        // produce correct results across many alternatives. (When
        // the gate flips on, this same test should keep passing.)
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public choose/2.
            choose(X, Y) :- member(X, [1, 2, 3]), member(Y, [a, b, c]).
            """);
        Assert.Equal(9, engine.QueryAll("choose(_, _).").Count());
    }

    /// <summary>Walks <paramref name="code"/> reading every
    /// <c>Call</c> instruction's <c>num_live_perms</c> operand
    /// (the int32 at offset +5 from the opcode byte).</summary>
    private static List<int> ExtractCallLivePerms(byte[] code)
    {
        var result = new List<int>();
        int pc = 0;
        while (pc < code.Length)
        {
            byte opByte = code[pc];
            if (opByte == (byte)Opcode.Call)
            {
                result.Add(BytecodeIO.ReadInt32(code, pc + 5));
            }
            var info = OpcodeTable.Get(opByte);
            if (!info.IsDefined || info.Size == 0) break;
            pc += info.Size;
        }
        return result;
    }
}

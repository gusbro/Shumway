using Shumway.Compiler.Ast;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 11 chunk 156: multi-arg dynamic indexed predicates use the
/// chunk-155-style extensible layout — every bucket chain at every
/// indexable level is a <c>try_me_else</c> / <c>retry_me_else</c>
/// chain with shared bodies via <c>execute</c>, and the runtime
/// chain-modification helpers walk multi-level dispatch via a
/// recursive enumerator (chunk-156's
/// <c>EnumerateChainHeadsRecursive</c>) so assertz / asserta /
/// retract / var-arg all go in-place across every level instead
/// of falling back to persistent rebuild.
///
/// <para>These tests exercise the multi-arg in-place paths
/// specifically — the chunk-155g tests cover overlap correctness
/// of the rebuild path; here we pin that the chunk-156 layout
/// actually takes effect and that mutations propagate to every
/// reachable chain.</para>
/// </summary>
public class Chunk156Tests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);
    private static int Fid(string n, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, arity);

    private static bool HasOpcode(byte[] code, Opcode target)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == target) return true;
            var info = OpcodeTable.Get(code[pc]);
            if (info.Size == 0) break;
            pc += info.Size;
        }
        return false;
    }

    [Fact]
    public void MultiArgDynamic_HotCompile_IsExtensibleLayout()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic kv/2.");
        e.Query("assertz(kv(a, 1)).");
        e.Query("assertz(kv(b, 2)).");
        e.Query("kv(a, _).");
        e.Query("kv(a, _).");
        Assert.True(e.DynamicPredicateCache.TryGetValue(Fid("kv", 2), out var cached));
        Assert.True(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm));
        Assert.True(HasOpcode(cached.Bytecode, Opcode.TryMeElse),
            "chunk-156: bucket chains use try_me_else (extensible) not Try");
        Assert.False(HasOpcode(cached.Bytecode, Opcode.Try),
            "chunk-156 replaces the contiguous Try with extensible chains");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.Execute),
            "shared bodies are reached via execute");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.EnterDynamic));
        Assert.True(HasOpcode(cached.Bytecode, Opcode.CheckVisible));
    }

    [Fact]
    public void MultiArgDynamic_AssertzSameKey_VisibleNextQuery()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic kv/2.");
        e.Query("assertz(kv(a, 1)).");
        e.Query("assertz(kv(b, 2)).");
        e.Query("kv(a, _).");
        e.Query("kv(a, _).");
        // Same-key assertz on multi-arg indexed predicate — chunk-
        // 156 path extends bucket(a) + final var chain in place.
        e.Query("assertz(kv(a, 9)).");
        var aSols = e.QueryAll("kv(a, X).").Select(s => ((IntTerm)s["X"]).Value).ToList();
        Assert.Equal(new long[] { 1, 9 }, aSols);
        var all = e.QueryAll("kv(_, _).").Count();
        Assert.Equal(3, all);
    }

    [Fact]
    public void MultiArgDynamic_AssertzNewKey_CreatesBucketAtLevel0()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic kv/2.");
        e.Query("assertz(kv(a, 1)).");
        e.Query("assertz(kv(b, 2)).");
        e.Query("kv(a, _).");
        e.Query("kv(a, _).");
        // New-key assertz with concrete arg 0 = c. Chunk-156c path
        // creates a new bucket at level 0, extends sub-switch table.
        e.Query("assertz(kv(c, 3)).");
        Assert.True(e.Query("kv(c, 3).").Success);
        Assert.True(e.Query("kv(a, 1).").Success);  // existing keys untouched
        Assert.True(e.Query("kv(b, 2).").Success);
    }

    [Fact]
    public void MultiArgDynamic_Retract_PatchesAllReachableChains()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic kv/2.");
        e.Query("assertz(kv(a, 1)).");
        e.Query("assertz(kv(b, 2)).");
        e.Query("assertz(kv(a, 9)).");
        e.Query("kv(a, _).");
        e.Query("kv(a, _).");
        e.Query("retract(kv(a, 1)).");
        // Retract on multi-arg goes through chunk-155d's recursive
        // chain enumerator — every chain entry referencing body of
        // kv(a,1) gets its died slot patched.
        Assert.True(e.Query("kv(a, 9).").Success);
        Assert.False(e.Query("kv(a, 1).").Success);
        Assert.True(e.Query("kv(b, 2).").Success);
    }

    [Fact]
    public void MultiArgDynamic_Asserta_DemotesAcrossLevels()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic kv/2.");
        e.Query("assertz(kv(a, 1)).");
        e.Query("assertz(kv(b, 2)).");
        e.Query("kv(a, _).");
        e.Query("kv(a, _).");
        e.Query("asserta(kv(c, 3)).");
        // Asserta on multi-arg via chunk-155f — for new-key 'c' the
        // path creates a new bucket and prepends to the final var
        // chain (demote + redirect).
        Assert.True(e.Query("kv(c, 3).").Success);
        var all = e.QueryAll("kv(X, _).").Select(s => s["X"]).ToList();
        // asserta puts new at the front of the var chain.
        Assert.Equal(new Term[] { Atom("c"), Atom("a"), Atom("b") }, all);
    }

    [Fact]
    public void MultiArgDynamic_VarArgAt0_ExtendsAllChainsAcrossLevels()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic p/2.");
        e.Query("assertz(p(a, 1)).");
        e.Query("assertz(p(b, 2)).");
        e.Query("p(a, _).");
        e.Query("p(a, _).");
        // Var-arg-at-0 clause on multi-arg — chunk-156's recursive
        // enumerator walks every chain across every level and
        // extends each.
        e.Query("assertz(p(_, generic)).");
        // For each existing bucket key, the var-arg clause should
        // surface as an extra solution.
        var aSols = e.QueryAll("p(a, X).").Select(s => s["X"]).ToList();
        Assert.Contains(Int(1), aSols);
        Assert.Contains(Atom("generic"), aSols);
        var bSols = e.QueryAll("p(b, X).").Select(s => s["X"]).ToList();
        Assert.Contains(Int(2), bSols);
        Assert.Contains(Atom("generic"), bSols);
    }

    [Fact]
    public void MultiArgDynamic_MixedMutations_StayConsistent()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic kv/2.");
        e.Query("assertz(kv(a, 1)).");
        e.Query("assertz(kv(b, 2)).");
        e.Query("kv(a, _).");
        e.Query("kv(a, _).");
        e.Query("assertz(kv(c, 3)).");
        e.Query("asserta(kv(a, 9)).");
        e.Query("retract(kv(b, 2)).");
        e.Query("assertz(kv(d, 4)).");
        var aSols = e.QueryAll("kv(a, X).").Select(s => ((IntTerm)s["X"]).Value).ToList();
        Assert.Equal(new long[] { 9, 1 }, aSols);
        Assert.False(e.Query("kv(b, _).").Success);
        Assert.True(e.Query("kv(c, 3).").Success);
        Assert.True(e.Query("kv(d, 4).").Success);
        Assert.Equal(4, e.QueryAll("kv(_, _).").Count());
    }
}

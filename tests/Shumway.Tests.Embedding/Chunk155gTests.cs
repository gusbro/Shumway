using Shumway.Compiler.Ast;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 155g: multi-arg dynamic indexed predicates. Chunks 155a–f
/// extend the in-place mutation paths only for the single-arg
/// indexed layout — multi-arg dynamic predicates (where the
/// compiler infers <c>indexableArgs.Count &gt; 1</c>) keep the
/// chunk-154 layout (contiguous <c>try</c> / <c>retry</c> /
/// <c>trust</c> + shared bodies + <c>switch_on_arg</c> for the
/// second and later levels) and fall through to the rebuild-on-
/// mutate fallback.
///
/// <para>This is a deliberate scope cut: the chunk-154 path is
/// correct (rebuild produces the right dispatch from current
/// <c>_dynamicClauses</c>), just slower for write-heavy multi-arg-
/// dynamic workloads. True in-place extensibility for multi-arg
/// indexed dispatch is recorded as Phase 11 work — it requires
/// multi-level switch traversal in every chain-modification helper
/// and a layout that nests extensible chains under each
/// <c>switch_on_arg</c> level. The tests here pin correctness of
/// the rebuild-fallback path so the chunk-155g contract — multi-
/// arg dynamics work, they just rebuild instead of extending in
/// place — survives any further refactor of the surrounding code.
/// </para>
/// </summary>
public class Chunk155gTests
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
    public void MultiArgDynamic_KeepsChunk154Layout_NotExtensible()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic shape/2.");
        e.Query("assertz(shape(circle, area)).");
        e.Query("assertz(shape(square, area)).");
        e.Query("assertz(shape(circle, perimeter)).");
        e.Query("shape(circle, area).");   // promote
        e.Query("shape(circle, area).");   // recompile indexed
        Assert.True(e.DynamicPredicateCache.TryGetValue(Fid("shape", 2), out var cached));
        // Chunk-154 layout: contiguous try / retry / trust opcodes,
        // not chunk-155a's try_me_else chain. Both SwitchOnTerm
        // (arg 0) and SwitchOnArg (arg 1) appear — the chunk-67
        // multi-arg indexing pattern.
        Assert.True(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm),
            "arg 0 indexed via switch_on_term");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.SwitchOnArg),
            "arg 1 indexed via switch_on_arg");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.Try),
            "chunk-154 layout uses Try (not TryMeElse)");
    }

    [Fact]
    public void MultiArgDynamic_AssertzCorrect_ViaRebuild()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic shape/2.");
        e.Query("assertz(shape(circle, area)).");
        e.Query("assertz(shape(square, area)).");
        e.Query("shape(circle, area).");
        e.Query("shape(circle, area).");
        // Add more clauses post-promotion. The chunk-154 fallback
        // rebuilds persistent on each mutation.
        e.Query("assertz(shape(circle, perimeter)).");
        e.Query("assertz(shape(triangle, area)).");
        // Concrete-arg dispatch finds the expected count.
        Assert.Equal(2, e.QueryAll("shape(circle, _).").Count());
        Assert.Equal(1, e.QueryAll("shape(square, _).").Count());
        Assert.Equal(1, e.QueryAll("shape(triangle, _).").Count());
        // Specific (arg0, arg1) lookup.
        Assert.True(e.Query("shape(circle, perimeter).").Success);
        Assert.True(e.Query("shape(triangle, area).").Success);
        // Var-arg-0 enumeration.
        Assert.Equal(4, e.QueryAll("shape(_, _).").Count());
    }

    [Fact]
    public void MultiArgDynamic_RetractCorrect_ViaRebuild()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic shape/2.");
        foreach (var v in new[] {
            "(circle, area)", "(square, area)", "(circle, perimeter)", "(triangle, area)"
        })
            e.Query($"assertz(shape{v}).");
        e.Query("shape(circle, area).");
        e.Query("shape(circle, area).");
        // Retract two distinct clauses post-promotion.
        e.Query("retract(shape(square, area)).");
        e.Query("retract(shape(triangle, area)).");
        Assert.False(e.Query("shape(square, _).").Success);
        Assert.False(e.Query("shape(triangle, _).").Success);
        Assert.True(e.Query("shape(circle, area).").Success);
        Assert.True(e.Query("shape(circle, perimeter).").Success);
        Assert.Equal(2, e.QueryAll("shape(_, _).").Count());
    }

    [Fact]
    public void MultiArgDynamic_AssertaCorrect_ViaRebuild()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic p/2.");
        e.Query("assertz(p(a, 1)).");
        e.Query("assertz(p(b, 2)).");
        e.Query("p(a, _).");
        e.Query("p(a, _).");
        e.Query("asserta(p(c, 3)).");
        // Order via var enumeration.
        var xs = e.QueryAll("p(X, _).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("c"), Atom("a"), Atom("b") }, xs);
    }

    [Fact]
    public void MultiArgDynamic_MixedMutations_StayConsistent()
    {
        // Stress: alternating assertz / asserta / retract on a
        // multi-arg dynamic predicate. Each mutation rebuilds via
        // the chunk-154 fallback; the live dispatch must always
        // reflect the current clause set.
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
        Assert.Equal(new long[] { 9, 1 }, aSols);   // asserta(a,9) then original (a,1)
        Assert.False(e.Query("kv(b, _).").Success);  // retracted
        Assert.True(e.Query("kv(c, 3).").Success);
        Assert.True(e.Query("kv(d, 4).").Success);
        // Full enumeration.
        var all = e.QueryAll("kv(_, _).").Count();
        Assert.Equal(4, all);
    }
}

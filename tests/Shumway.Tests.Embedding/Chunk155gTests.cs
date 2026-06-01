using Shumway.Compiler.Ast;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 155g / 156: multi-arg dynamic indexed predicates.
/// Originally (chunk 155g) multi-arg dynamic predicates kept the
/// chunk-154 contiguous <c>try</c> / <c>retry</c> / <c>trust</c>
/// layout and fell through to rebuild-on-mutate. Phase 11 chunk
/// 156 lifts that fallback: every chunk-155-style mutation path
/// (assertz / asserta / retract / var-arg / new-key) now walks
/// multi-level dispatch via a recursive chain-head enumerator,
/// and the compiled layout uses extensible <c>try_me_else</c> /
/// <c>retry_me_else</c> chains at every level of indexing.
/// The tests originally pinned the chunk-154 layout; they're now
/// updated to verify the chunk-156 layout.
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
    public void MultiArgDynamic_UsesChunk156ExtensibleLayout()
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
        // Chunk-156 layout: extensible try_me_else chains at every
        // level, multi-arg dispatch via switch_on_term (arg 0) +
        // switch_on_arg (arg 1).
        Assert.True(HasOpcode(cached!.Bytecode, Opcode.SwitchOnTerm),
            "arg 0 indexed via switch_on_term");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.SwitchOnArg),
            "arg 1 indexed via switch_on_arg");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.TryMeElse),
            "chunk-156 layout uses TryMeElse (not Try)");
        Assert.True(HasOpcode(cached.Bytecode, Opcode.Execute),
            "shared bodies are reached via execute");
        Assert.False(HasOpcode(cached.Bytecode, Opcode.Try),
            "chunk-156 removes the contiguous Try opcode");
    }

    [Fact]
    public void MultiArgDynamic_AssertzCorrect_InPlace()
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
        Assert.Single(e.QueryAll("shape(square, _)."));
        Assert.Single(e.QueryAll("shape(triangle, _)."));
        // Specific (arg0, arg1) lookup.
        Assert.True(e.Query("shape(circle, perimeter).").Success);
        Assert.True(e.Query("shape(triangle, area).").Success);
        // Var-arg-0 enumeration.
        Assert.Equal(4, e.QueryAll("shape(_, _).").Count());
    }

    [Fact]
    public void MultiArgDynamic_RetractCorrect_InPlace()
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
    public void MultiArgDynamic_AssertaCorrect_InPlace()
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
        var aSols = e.QueryAll("kv(a, X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 9, 1 }, aSols);   // asserta(a,9) then original (a,1)
        Assert.False(e.Query("kv(b, _).").Success);  // retracted
        Assert.True(e.Query("kv(c, 3).").Success);
        Assert.True(e.Query("kv(d, 4).").Success);
        // Full enumeration.
        var all = e.QueryAll("kv(_, _).").Count();
        Assert.Equal(4, all);
    }
}

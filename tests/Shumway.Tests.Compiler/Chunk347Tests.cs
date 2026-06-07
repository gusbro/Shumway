using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler;

/// <summary>
/// Chunk 347 (Phase 28): WAM void-batching. A run of consecutive anonymous
/// (singleton) arguments in a compound now compiles to a single
/// <c>unify_void(N)</c> instruction instead of N separate <c>unify_void 1</c>s,
/// matching what GNU Prolog's <c>pl2wam</c> emits for the same shape. This was
/// the one outlier (zebra, <c>house(red, english, _, _, _)</c>) in the
/// SWI/van-Roy WAM-vs-GProlog comparison — see
/// <c>docs/wam-vs-gprolog-bench.md</c>. The interpreter already decoded the
/// operand as a count, so this is a pure codegen-density win.
/// </summary>
public class Chunk347Tests
{
    private static string DisasmOf(string source, string name, int arity)
    {
        var entries = PredicateDisassembler.Disassemble(source);
        var entry = entries.Single(e => e.Name == name && e.Arity == arity);
        Assert.Null(entry.Error);
        return entry.Text;
    }

    [Fact]
    public void ConsecutiveVoids_BatchIntoOne()
    {
        // f/5 head match: a, then three anonymous, then b. The three voids in
        // the middle must coalesce into a single unify_void(3).
        string text = DisasmOf("p(f(a, _, _, _, b)).", "p", 1);

        int count = text.Split('\n')
            .Count(line => line.Contains("unify_void"));
        Assert.Equal(1, count);

        // The single instruction carries the batch count 3.
        string voidLine = text.Split('\n').Single(l => l.Contains("unify_void"));
        Assert.Contains("3", voidLine);
    }

    [Fact]
    public void NonConsecutiveVoids_StaySeparate()
    {
        // Anonymous slots split by a real argument: two distinct runs of one,
        // so two unify_void instructions (each count 1) — batching must not
        // merge across the intervening unify_constant.
        string text = DisasmOf("q(g(_, x, _)).", "q", 1);

        int count = text.Split('\n')
            .Count(line => line.Contains("unify_void"));
        Assert.Equal(2, count);
    }
}

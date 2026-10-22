using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>ADR-063: a compound of a clause body with at least the
/// threshold's cells is copied from an image (<c>put_blit</c>) instead of
/// built, its variables as holes the copy fills from registers or points
/// registers at; a smaller one, one with holes for more than half its
/// cells, or one with a big integer, is built as before.</summary>
public class TermImageEmissionTests
{
    private static string Dis(string src) =>
        PredicateDisassembler.Disassemble(src).First(e => e.Name == "p").Text;

    /// <summary>The images the clauses of <paramref name="src"/> intern.</summary>
    private static IReadOnlyList<TermImage> Images(string src)
    {
        var pools = new LiteralPools();
        var compiler = new ClauseCompiler();
        foreach (var clause in new ClauseReader(src).ReadAll())
            if (clause.Kind is ClauseKind.Rule or ClauseKind.Fact)
                compiler.Compile(clause, pools.Strings, pools.Floats, pools.BigInts, pools.Images);
        return pools.Images.Items;
    }

    [Fact]
    public void AGroundListPastTheThresholdIsCopiedFromAnImage()
    {
        // Ten atoms: twenty cells, one instruction.
        string text = Dis("p :- q([a, b, c, d, e, f, g, h, i, j]).");
        Assert.Contains("put_blit", text);
        Assert.DoesNotContain("put_list", text);
        Assert.DoesNotContain("unify_", text);
    }

    [Fact]
    public void TheThresholdCountsCells()
    {
        Assert.Equal(8, ClauseCompiler.ImageThreshold);   // the default these sizes assume
        Assert.Contains("put_blit", Dis("p :- q(f(a, b, c, d, e, f, g))."));
        Assert.DoesNotContain("put_blit", Dis("p :- q(f(a, b, c, d, e, f))."));
    }

    [Fact]
    public void AVariableSeenBeforeIsAHoleTheRegisterFills()
    {
        string src = "p(X) :- q([a, b, c, d, e, f, g, h, i, X]).";
        string text = Dis(src);
        Assert.Contains("put_blit", text);
        Assert.DoesNotContain("put_list", text);
        var image = Assert.Single(Images(src));
        // Ten cell pairs; X is the head of the last, in the register the
        // head's X moved to (A0 is the call's).
        var hole = Assert.Single(image.Holes.ToArray());
        Assert.False(hole.IsOut);
        Assert.False(hole.IsY);
        Assert.Equal(18, hole.Offset);
        Assert.Equal(Cell.Ref(18), image.Cells[18]);
    }

    [Fact]
    public void AVariableFirstSeenInTheImageIsAHoleTheCopyPointsAt()
    {
        var image = Assert.Single(Images("p :- q(f(X, a, b, c, d, e, f), X)."));
        var hole = Assert.Single(image.Holes.ToArray());
        Assert.True(hole.IsOut);
        Assert.False(hole.IsY);
        Assert.Equal(1, hole.Offset);
    }

    [Fact]
    public void ARepeatedVariableIsAnOutHoleAndInHolesAtItsOtherCells()
    {
        var image = Assert.Single(Images("p :- q(f(X, X, a, b, c, d, X))."));
        var holes = image.Holes.ToArray();
        Assert.Equal(3, holes.Length);
        Assert.True(holes[0].IsOut);
        Assert.Equal(1, holes[0].Offset);
        Assert.All(holes.Skip(1), h => Assert.False(h.IsOut));
        Assert.All(holes, h => Assert.Equal(holes[0].Slot, h.Slot));
        Assert.Equal(new[] { 2, 7 }, holes.Skip(1).Select(h => h.Offset).OrderBy(o => o));
        foreach (int at in new[] { 1, 2, 7 })
            Assert.Equal(Cell.Ref(at), image.Cells[at]);
    }

    [Fact]
    public void ADeferredPartsMarkNamesItsBlock()
    {
        // The builder lays i, h, g after the root: g's block at 11, h's at 8.
        string src = "p(f(g(a, b), h(c, d), i(e, f, g))).";
        Assert.Contains("get_blit", Dis(src));
        var image = Assert.Single(Images(src));
        var marks = image.Holes.ToArray().Where(h => h.IsSentinel).Select(h => h.Offset).OrderBy(o => o).ToArray();
        Assert.Equal(new[] { 8, 11 }, marks);
        Assert.Equal(Cell.Str(11), image.Cells[1]);
        Assert.Equal(Cell.Str(8), image.Cells[2]);
    }

    [Fact]
    public void AVariablesOutHoleIsAtTheCellTheStreamMeetsFirst()
    {
        // The builder lays i, h, g after the root; the stream runs i inline,
        // then the deferred parts g and h. X's Out hole is g's cell (9), h's
        // cell (7) an In hole after it; Z's is i's (5).
        string src = "p(f(g(X), h(X), i(Z))) :- q(X, Z).";
        Assert.Contains("get_blit", Dis(src));
        var image = Assert.Single(Images(src));
        var holes = image.Holes.ToArray().Where(h => !h.IsSentinel).ToArray();
        Assert.Equal(3, holes.Length);
        var x = holes.Where(h => h.Offset is 7 or 9).ToArray();
        Assert.Equal(2, x.Length);
        Assert.Equal(x[0].Slot, x[1].Slot);
        Assert.True(Assert.Single(x, h => h.Offset == 9).IsOut);
        Assert.False(Assert.Single(x, h => h.Offset == 7).IsOut);
        Assert.True(Assert.Single(holes, h => h.Offset == 5).IsOut);
        int lastOut = Array.FindLastIndex(holes, h => h.IsOut);
        int firstIn = Array.FindIndex(holes, h => !h.IsOut);
        Assert.True(lastOut < firstIn);
    }

    [Fact]
    public void AnAnonymousVariableIsAFreshCellAndNoHole()
    {
        var image = Assert.Single(Images("p :- q([h(_, _), h(_, _), h(_, _)])."));
        Assert.False(image.HasHoles);
        Assert.Equal(15, image.Length);
        // Every anonymous variable is a REF to its own cell: unbound once relocated.
        int fresh = 0;
        for (int i = 0; i < image.Length; i++)
            if (image.Cells[i] == Cell.Ref(i)) fresh++;
        Assert.Equal(6, fresh);
    }

    [Fact]
    public void APermanentVariableIsAHoleOfTheFrame()
    {
        // Y lives in the frame: seen by r/1 before the image, the copy reads
        // the frame; first seen in the image, the copy writes the frame.
        var seen = Assert.Single(Images("p :- r(Y), q(f(Y, a, b, c, d, e, f)), s(Y)."));
        var h = Assert.Single(seen.Holes.ToArray());
        Assert.True(h.IsY);
        Assert.False(h.IsOut);
        var first = Assert.Single(Images("p :- q(f(Y, a, b, c, d, e, f)), s(Y)."));
        h = Assert.Single(first.Holes.ToArray());
        Assert.True(h.IsY);
        Assert.True(h.IsOut);
    }

    [Fact]
    public void HolesForMoreThanHalfTheCellsKeepTheBuild()
    {
        string src = "p :- q(f(A, B, C, D, E, F, G)).";
        Assert.Empty(Images(src));
        Assert.Contains("put_structure", Dis(src));
    }

    [Fact]
    public void AnArgumentOfACompoundTheImageCannotCarryGoesToATemporary()
    {
        // g's big integer keeps g built; its list is past the threshold: the
        // image goes to a temporary and unify_value_x puts it in place.
        string text = Dis("p(X) :- q(g(X, 100000000000000000000, [a, b, c, d, e, f, g, h])).");
        Assert.Contains("put_blit", text);
        Assert.Contains("unify_value_x", text);
        Assert.DoesNotContain("put_list", text);
    }

    [Fact]
    public void AStringAndAFloatLiveInsideAnImage()
    {
        string text = Dis("p :- q(f(\"hello\", 1.5, a, b, c)).");
        Assert.Contains("put_blit", text);
        Assert.DoesNotContain("put_pstr", text);
        Assert.DoesNotContain("put_float", text);
    }

    [Fact]
    public void AHeadKeepsItsMatching()
    {
        // A head's stream may run in read mode: no put, and the matching
        // stream stays behind the get_blit prefix for the bound case.
        string text = Dis("p([a, b, c, d, e, f, g, h, i, j]).");
        Assert.DoesNotContain("put_blit", text);
        Assert.Contains("get_blit", text);
        Assert.Contains("get_list", text);
        string inline = Dis("p(X) :- X = [a, b, c, d, e, f, g, h, i, j].");
        Assert.DoesNotContain("put_blit", inline);
    }

    private static int Count(string text, string word)
        => (text.Length - text.Replace(word, "").Length) / word.Length;

    [Fact]
    public void AHeadArgumentGetsItsImageBeforeItsStreamAndItsDeferredPartsASkip()
    {
        // Two arguments past the threshold; the first has two deferred parts
        // (non-last nested compounds), the second none. The stream order
        // stays breadth-first: both prefixes, both streams, then the parts.
        string src = "p(a(b(c, d), e)-f(g), h-i(j, k(l(m))), OUT) :- q(OUT).";
        string text = Dis(src);
        Assert.Equal(2, Count(text, "get_blit"));
        Assert.Equal(2, Count(text, "skip_if_copied"));
        Assert.Contains("get_structure", text);
        var images = Images(src);
        Assert.Equal(2, images.Count);
        Assert.Equal(2, images[0].Holes.ToArray().Count(h => h.IsSentinel));
        Assert.False(images[1].HasHoles);
    }

    [Fact]
    public void AHeadVariableFirstCapturedInALaterArgumentKeepsTheStream()
    {
        // X is first met, in stream order, inside the second argument's own
        // stream: the first argument's copy could not know its register and
        // the stream would overwrite it. The second argument has its image.
        string src = "p(f(h(X), a, b, c, d, e, f), g(X, a, b, c, d, e, f)).";
        Assert.Equal(1, Count(Dis(src), "get_blit"));
        var image = Assert.Single(Images(src));
        var hole = Assert.Single(image.Holes.ToArray());
        Assert.True(hole.IsOut);
    }

    [Fact]
    public void AHeadVariableMetAsAPlainArgumentIsAHoleFromItsRegister()
    {
        // X is the second argument itself: its register holds the call's
        // argument throughout the head, before or after the compound.
        var image = Assert.Single(Images("p(f(X, a, b, c, d, e, f), X)."));
        Assert.Equal(new ImageHole(1, ImageHole.In, 1), Assert.Single(image.Holes.ToArray()));
        image = Assert.Single(Images("p(X, f(X, a, b, c, d, e, f))."));
        Assert.Equal(new ImageHole(1, ImageHole.In, 0), Assert.Single(image.Holes.ToArray()));
    }

    [Fact]
    public void ADeferredPartPastTheThresholdGetsItsOwnImageAndMatcher()
    {
        // k's first argument is a sixteen-cell part in a non-last position: a
        // unify_blit before its stream; the argument itself has its get_blit,
        // and its image marks the part's temporary.
        string src = "p(k(q(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o), z), N) :- r(N).";
        string text = Dis(src);
        Assert.Equal(1, Count(text, "get_blit"));
        Assert.Equal(1, Count(text, "unify_blit"));
        Assert.Equal(0, Count(text, "skip_if_copied"));
        var images = Images(src);
        Assert.Equal(2, images.Count);
        Assert.Single(images[0].Holes.ToArray(), h => h.IsSentinel);
        Assert.False(images[1].HasHoles);
    }

    [Fact]
    public void APartsVariablesAreHolesOfItsOwnOrOfAnEarlierStream()
    {
        // X is first met in the argument's own stream (r(X, Y) is inline), so
        // the part q reads it from the register that stream gave it; Y is a
        // plain argument. The argument's image writes X and reads Y.
        string src = "p(p(q(X, b, c, d, e, f, g, h, i, j, k, l, m, n, o), r(X, Y)), Y) :- s(Y).";
        string text = Dis(src);
        Assert.Equal(1, Count(text, "get_blit"));
        Assert.Equal(1, Count(text, "unify_blit"));
        var images = Images(src);
        Assert.Equal(2, images.Count);
        var arg = images[0].Holes.ToArray();
        Assert.Contains(arg, h => h.IsOut && !h.IsSentinel);
        Assert.Contains(arg, h => !h.IsOut && !h.IsSentinel);
        Assert.Contains(arg, h => h.IsSentinel);
        var part = Assert.Single(images[1].Holes.ToArray());
        Assert.False(part.IsOut);
    }

    [Fact]
    public void APartIsMatchedUnderAnArgumentThatKeepsItsStream()
    {
        // X is first met in the second argument's own stream, after the first
        // argument's prefix would run: that argument keeps its stream. The
        // part q runs after both streams and reads X from its register.
        string src = "p(p(q(X, b, c, d, e, f, g, h, i, j, k, l, m, n, o), w), g(X)) :- s(X).";
        string text = Dis(src);
        Assert.Equal(0, Count(text, "get_blit"));
        Assert.Equal(1, Count(text, "unify_blit"));
        Assert.Equal(0, Count(text, "skip_if_copied"));
        var image = Assert.Single(Images(src));
        var hole = Assert.Single(image.Holes.ToArray());
        Assert.False(hole.IsOut);
    }

    [Fact]
    public void AHeadSubTermSharedWithAnArgumentKeepsTheStream()
    {
        // The second argument holds a copy of the first: the stream references
        // the first's register (CSE) where an image would build it again.
        string src = "p(f(a, b, c, d, e, f, g, h), g(f(a, b, c, d, e, f, g, h), x)) :- q.";
        string text = Dis(src);
        Assert.Equal(1, Count(text, "get_blit"));
        Assert.Contains("unify_value_x", text);
        Assert.Single(Images(src));
    }

    /// <summary>A head that holds one compound object in two places (an AST
    /// may share a sub-term) gets no image: the plan keys a term by its
    /// object, and the two places took one prefix, the first naming
    /// whatever image the pool held at id 0.</summary>
    [Fact]
    public void AHeadHoldingOneObjectTwiceKeepsItsStreams()
    {
        static Term A(string name) => new AtomTerm(name);
        var shared = new CompoundTerm("f", new Term[] { A("a"), A("b"), A("c"), A("d"), A("e"), A("f"), A("g"), A("h"), A("i") });
        var heads = new Term[]
        {
            new CompoundTerm("p", new Term[] { shared, shared }),
            new CompoundTerm("p", new Term[]
            {
                new CompoundTerm("k", new Term[] { shared, A("z") }),
                new CompoundTerm("k", new Term[] { shared, A("y") }),
            }),
        };
        foreach (var head in heads)
        {
            var pools = new LiteralPools();
            var compiler = new ClauseCompiler();
            // Another image at id 0, as a program's pool has.
            compiler.Compile(new ClauseReader("o :- q(g(a, b, c, d, e, f, g, h, i)).").ReadAll().First(),
                pools.Strings, pools.Floats, pools.BigInts, pools.Images);
            var clause = compiler.Compile(Clause.From(head), pools.Strings, pools.Floats, pools.BigInts, pools.Images);
            foreach (var ins in Disassembler.Iterate(clause.Bytecode, 0, clause.Bytecode.Length))
                Assert.False(ins.Op is Opcode.GetBlit or Opcode.UnifyBlit, $"{head}: {ins}");
        }
    }

    [Fact]
    public void TheHeadsSwitchKeepsTheStreamAlone()
    {
        bool prev = ClauseCompiler.ImageHeads;
        ClauseCompiler.ImageHeads = false;
        try
        {
            string text = Dis("p([a, b, c, d, e, f, g, h, i, j]).");
            Assert.DoesNotContain("get_blit", text);
            Assert.Contains("get_list", text);
        }
        finally { ClauseCompiler.ImageHeads = prev; }
    }

    [Fact]
    public void ADeferredPartOfABodyBuildIsCopiedToo()
    {
        // g's first argument is a nested compound in a non-last position with
        // a big integer beside it, so neither is g an image nor the tree a
        // reserved build: the list is deferred onto a temporary and built
        // there, from its image.
        string text = Dis("p(X) :- q(g([a, b, c, d, e, f, g, h], 100000000000000000000, X)).");
        Assert.Contains("put_blit", text);
        Assert.Contains("put_structure", text);
    }

    [Fact]
    public void ALongListIsOneImageAndNoStack()
    {
        // The builder keeps no call per nesting level: a list this long
        // overflowed the compiler's stack when each tail was a call.
        string list = string.Join(",", System.Linq.Enumerable.Range(1, 200_000));
        string text = Dis($"p :- q([{list}]).");
        Assert.Contains("put_blit", text);
        Assert.DoesNotContain("put_list", text);
    }

    [Fact]
    public void ALongLeftNestedChainIsOneImageAndNoStack()
    {
        // 1+2+...+N nests in the first argument, not the last: the spine of
        // a list is not the only deep shape.
        string chain = string.Join("+", System.Linq.Enumerable.Range(1, 200_000));
        string text = Dis($"p :- q({chain}).");
        Assert.Contains("put_blit", text);
        Assert.DoesNotContain("put_structure", text);
    }

    [Fact]
    public void ABigIntegerKeepsTheBuild()
    {
        string text = Dis("p :- q(f(100000000000000000000, a, b, c, d, e, f, g)).");
        Assert.DoesNotContain("put_blit", text);
        Assert.Contains("bigint", text);
    }
}

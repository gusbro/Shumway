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
    public void ARepeatedVariableIsOneHoleAndAReferenceToItsFirstCell()
    {
        var image = Assert.Single(Images("p :- q(f(X, X, a, b, c, d, X))."));
        Assert.Single(image.Holes.ToArray());
        Assert.Equal(Cell.Ref(1), image.Cells[1]);
        Assert.Equal(Cell.Ref(1), image.Cells[2]);
        Assert.Equal(Cell.Ref(1), image.Cells[7]);
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

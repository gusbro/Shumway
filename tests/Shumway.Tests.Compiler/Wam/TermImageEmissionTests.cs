using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>ADR-063: a ground compound of a clause body with at least the
/// threshold's cells is copied from an image (<c>put_blit</c>) instead of
/// built; a smaller one, or one with a variable or a big integer, is built
/// as before.</summary>
public class TermImageEmissionTests
{
    private static string Dis(string src) =>
        PredicateDisassembler.Disassemble(src).First(e => e.Name == "p").Text;

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
    public void AVariableKeepsTheBuild()
    {
        string text = Dis("p(X) :- q([a, b, c, d, e, f, g, h, i, X]).");
        Assert.DoesNotContain("put_blit", text);
        Assert.Contains("put_list", text);
    }

    [Fact]
    public void AGroundArgumentOfACompoundGoesToATemporary()
    {
        // g(X, [...]) is not ground; its list is, and past the threshold: the
        // image goes to a temporary and unify_value_x puts it in place.
        string text = Dis("p(X) :- q(g(X, [a, b, c, d, e, f, g, h])).");
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
        // A head's stream may run in read mode: an image there would allocate
        // and unify where the stream matches in place.
        string text = Dis("p([a, b, c, d, e, f, g, h, i, j]).");
        Assert.DoesNotContain("put_blit", text);
        Assert.Contains("get_list", text);
        string inline = Dis("p(X) :- X = [a, b, c, d, e, f, g, h, i, j].");
        Assert.DoesNotContain("put_blit", inline);
    }

    [Fact]
    public void ADeferredPartOfABodyBuildIsCopiedToo()
    {
        // g's first argument is a nested compound in a non-last position with
        // a float beside it, so the tree is not a reserved build: the list is
        // deferred onto a temporary and built there, from its image.
        string text = Dis("p(X) :- q(g([a, b, c, d, e, f, g, h], 1.5, X)).");
        Assert.Contains("put_blit", text);
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

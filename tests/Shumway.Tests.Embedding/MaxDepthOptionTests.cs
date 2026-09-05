using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Where <c>max_depth(N)</c> counts a level. An operand of an operator
/// is one whatever it is, so <c>1:2</c> at depth 1 is <c>... : ...</c> and the
/// option can no longer be defeated by writing the term with operators. An
/// argument of a canonical compound and an element of a list are not, so an
/// atom sitting at the limit still prints: eliding it would cost the reader the
/// datum and save nothing.</summary>
public sealed class MaxDepthOptionTests
{
    private static string Written(string term, int depth)
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            $"with_output_to(atom(A), write_term({term}, "
            + $"[max_depth({depth}), quoted(true)])).");
        Assert.True(sol.Success, term);
        return ((AtomTerm)sol["A"]!).Name;
    }

    [Theory]
    [InlineData("1:2", 1, "... : ...")]
    [InlineData("1:f(a)", 1, "... : ...")]
    [InlineData("1+2*3", 2, "1+ ... * ...")]
    public void AnOperandIsALevel(string term, int depth, string expected)
        => Assert.Equal(expected, Written(term, depth));

    [Theory]
    [InlineData("f(a,b)", 1, "f(a,b)")]
    [InlineData("[1,2,3,4,5]", 3, "[1,2,3|...]")]
    [InlineData("a(b(c(d(e))))", 3, "a(b(c(...)))")]
    [InlineData("[1,[2,[3,[4]]]]", 3, "[1,[2,[3|...]]]")]
    public void AnArgumentAndAnElementAreNot(string term, int depth, string expected)
        => Assert.Equal(expected, Written(term, depth));

    [Fact]
    public void ZeroIsNoLimit()
    {
        Assert.Equal("f(g(h))", Written("f(g(h))", 0));
        Assert.Equal("1:2:3", Written("1:2:3", 0));
    }

    [Fact]
    public void NothingIsElidedThatFits()
    {
        // The tail of a partial list is not more list, so there is nothing
        // beyond the limit to stand for.
        Assert.Matches(@"^\[a,b\|_", Written("[a,b|_T]", 2));
    }
}

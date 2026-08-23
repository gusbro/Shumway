using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>
/// between/3 with a BigInteger bound on either side (Trealla issue #1105):
/// degenerate and short bigint ranges check and enumerate, and values
/// crossing back under the 60-bit inline range demote to fixnum cells.
/// </summary>
public class BetweenBigIntTests
{
    [Theory]
    [InlineData("X is 4^4^4, between(X, X, X)")]
    [InlineData("X is 4^4^4, Hi is X + 3, findall(D, (between(X, Hi, V), D is V - X), L), L == [0, 1, 2, 3]")]
    [InlineData("X is 4^4^4, Hi is X + 3, \\+ between(X, Hi, 0)")]
    [InlineData("NB1 is -(2^63) - 1, NB2 is NB1 + 3, findall(V, between(NB1, NB2, V), L), length(L, 4), last(L, Last), Last is NB1 + 3")]
    [InlineData("Big is 2^70, once(between(1, Big, V)), V == 1")]
    public void BigintBoundsCheckAndEnumerate(string goal)
    {
        var e = new PrologEngine();
        Assert.True(e.Query($"{goal}.").Success);
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Labeling steps through a domain's values, it never writes them
/// out: a variable in 0..3000000000 is labeled as one in 0..9 is.
///
/// <para>Every value order built the list of all the values first, and a
/// wide domain ended in an OutOfMemoryException of the host. SICStus and
/// Scryer answer X = 0 at once (3000000000 with down).</para></summary>
public sealed class ClpfdWideDomainLabelingTests
{
    private static PrologEngine Fd()
    {
        var e = new PrologEngine();
        e.UseClpfd();
        return e;
    }

    private static string Value(string goal, string var = "X")
    {
        var e = Fd();
        var s = e.Query(goal + ".");
        Assert.True(s.Success, goal);
        return AstTermRenderer.Render(s[var]!, 400, e.Operators).Replace(" ", "");
    }

    [Theory]
    [InlineData("X in 0..3000000000, label([X])", "0")]
    [InlineData("X in 0..3000000000, labeling([up], [X])", "0")]
    [InlineData("X in 0..3000000000, labeling([down], [X])", "3000000000")]
    [InlineData("X in 0..3000000000, labeling([middle], [X])", "1499999999")]
    [InlineData("X in 0..3000000000, labeling([ff], [X])", "0")]
    [InlineData("X in 0..3000000000, indomain(X)", "0")]
    [InlineData("X in -576460752303423488..576460752303423487, labeling([down], [X])", "576460752303423487")]
    [InlineData("X in -576460752303423488..576460752303423487, label([X])", "-576460752303423488")]
    [InlineData("X in -576460752303423488..576460752303423487, labeling([middle], [X])", "-1")]
    public void AWideDomain_GivesItsFirstValueAtOnce(string goal, string value)
        => Assert.Equal(value, Value(goal));

    [Fact]
    public void RandomValueOnAWideDomain_GivesAValueOfIt()
        => Assert.True(Fd().Query(
            "X in 0..3000000000, labeling([random_value], [X]), integer(X), X >= 0, X =< 3000000000.").Success);

    [Theory]
    [InlineData("labeling([up], [X])", "[2999999991,2999999992,2999999993]")]
    [InlineData("labeling([down], [X])", "[3000000000,2999999999,2999999998]")]
    [InlineData("indomain(X)", "[2999999991,2999999992,2999999993]")]
    public void TheNextValues_ComeOnBacktracking(string labeling, string first3)
        => Assert.Equal(first3, Value(
            $"X in 0..3000000000, X #> 2999999990, findall(X, {labeling}, L), L = [A, B, C | _], T = [A, B, C]",
            "T"));

    // The orders the stepping keeps, a domain with holes included: the
    // midpoint walk takes the nearer side first, the lower one on a tie.
    [Theory]
    [InlineData("1..9, X #\\= 4, X #\\= 5", "middle", "[3,6,2,7,1,8,9]")]
    [InlineData("1..9, X #\\= 5", "down", "[9,8,7,6,4,3,2,1]")]
    [InlineData("1..9, X #\\= 5", "up", "[1,2,3,4,6,7,8,9]")]
    [InlineData("5..5", "middle", "[5]")]
    [InlineData("-3..3, X #\\= 0", "middle", "[-1,-2,1,-3,2,3]")]
    public void AnOrderOverADomainWithHoles(string domain, string order, string values)
        => Assert.Equal(values, Value($"X in {domain}, findall(X, labeling([{order}], [X]), L)", "L"));

    [Fact]
    public void RandomValueOffersEveryValueOnce_ADomainWithHolesIncluded()
        => Assert.True(Fd().Query(
            "X in 1..20, X #\\= 7, X #\\= 13, findall(X, labeling([random_value], [X]), L), "
            + "length(L, 18), msort(L, S), length(S, 18), \\+ memberchk(7, L), \\+ memberchk(13, L).").Success);

    [Fact]
    public void AnUnboundedDomain_IsStillAnInstantiationError()
    {
        var e = Fd();
        Assert.True(e.Query("X #> 0, catch(label([X]), error(instantiation_error, _), true).").Success);
        Assert.True(e.Query("X #> 0, catch(indomain(X), error(instantiation_error, _), true).").Success);
    }
}

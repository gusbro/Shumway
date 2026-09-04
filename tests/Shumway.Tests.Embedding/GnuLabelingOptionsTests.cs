using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>fd_labeling/2</c>, the GNU spelling of a labeling option list,
/// which this library did not have at all (existence_error). Its
/// <c>variable_method</c> / <c>value_method</c> wrappers map onto the
/// strategies this solver implements; a heuristic it does not implement is
/// REFUSED rather than quietly replaced, since which solution comes first is
/// exactly what a labeling option is chosen for.</summary>
public class GnuLabelingOptionsTests
{
    private static PrologEngine Clpfd()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query("use_module(library(clpfd)).").Success);
        return e;
    }

    [Theory]
    [InlineData("fd_domain([X,Y],1,3), fd_labeling([X,Y], []), X == 1, Y == 1")]
    [InlineData("fd_domain([X,Y],1,3), fd_labeling([X,Y], [value_method(min)]), X == 1")]
    [InlineData("fd_domain([X,Y],1,3), fd_labeling([X,Y], [value_method(max)]), X == 3, Y == 3")]
    [InlineData("fd_domain([X,Y],1,3), fd_labeling([X,Y], [variable_method(ff)]), X == 1")]
    [InlineData("fd_domain([X,Y],1,3), fd_labeling([X,Y], [variable_method(first_fail)]), X == 1")]
    [InlineData("fd_domain([X,Y],1,3), fd_labeling([X,Y], [variable_method(standard)]), X == 1")]
    // A single FD variable is a valid argument, as in GNU Prolog.
    [InlineData("fd_domain(X,1,3), fd_labeling(X, [value_method(max)]), X == 3")]
    public void TheSupportedOptionsSteerTheSearch(string goal)
        => Assert.True(Clpfd().Query(goal + ".").Success);

    [Theory]
    // GNU's own domain name for a bad option.
    [InlineData("fd_domain([X],1,3), fd_labeling([X], [bogus])",
                "domain_error(fd_labeling_option, bogus)")]
    // Honest about a heuristic we do not implement: it changes which
    // solution comes first, so silently substituting another would answer a
    // different question.
    [InlineData("fd_domain([X],1,3), fd_labeling([X], [value_method(bisect)])",
                "domain_error(fd_labeling_option, value_method(bisect))")]
    [InlineData("fd_domain([X],1,3), fd_labeling([X], [variable_method(max_regret)])",
                "domain_error(fd_labeling_option, variable_method(max_regret))")]
    public void AnOptionItCannotHonourIsRefused(string goal, string expected)
    {
        var e = Clpfd();
        Assert.True(e.Query(
            $"catch(({goal}), error(E, _), true), E = {expected}.").Success);
    }

    [Theory]
    [InlineData("fd_labeling(V, [])")]
    [InlineData("fd_domain([X],1,3), fd_labeling([X], [Opt])")]
    [InlineData("fd_domain([X],1,3), fd_labeling([X], Opts)")]
    public void AnUninstantiatedArgumentIsReportedAsSuch(string goal)
    {
        var e = Clpfd();
        Assert.True(e.Query(
            $"catch(({goal}), error(E, C), true), E == instantiation_error, "
            + "C == fd_labeling/2.").Success);
    }

    [Theory]
    // The counting constraints, which are the only capability of this
    // family with no native spelling: how many variables of a list take a
    // value. They share one implementation, kept internal because the
    // general form's established name (SICStus's count/4) belongs to
    // another naming convention than the one this library follows.
    [InlineData("fd_atmost(1, [X,Y,Z], 1)", "[0-0-0,0-0-1,0-1-0,1-0-0]")]
    [InlineData("fd_atleast(2, [X,Y,Z], 1)", "[0-1-1,1-0-1,1-1-0,1-1-1]")]
    [InlineData("fd_exactly(2, [X,Y,Z], 1)", "[0-1-1,1-0-1,1-1-0]")]
    public void TheCountingConstraintsHold(string goal, string expected)
    {
        var e = Clpfd();
        Assert.True(e.Query(
            $"[X,Y,Z] ins 0..1, {goal}, findall(X-Y-Z, label([X,Y,Z]), L), "
            + $"L == {expected}.").Success);
    }

    [Fact]
    public void TheCountItselfMayBeUnbound()
    {
        // A constraint, not a check: the count runs in either direction.
        Assert.True(Clpfd().Query(
            "[X,Y] ins 0..1, fd_exactly(N, [X,Y], 1), X = 1, Y = 1, N == 2.").Success);
    }

    [Theory]
    [InlineData("fd_atmost(1, L, 1)", "fd_atmost/3")]
    [InlineData("fd_atleast(1, L, 1)", "fd_atleast/3")]
    [InlineData("fd_exactly(1, L, 1)", "fd_exactly/3")]
    public void EachCountingSpellingReportsItself(string goal, string indicator)
    {
        Assert.True(Clpfd().Query(
            $"catch(({goal}), error(E, C), true), E == instantiation_error, "
            + $"C == {indicator}.").Success);
    }

    [Theory]
    // A value that is not a truth value is out of the DOMAIN of reifiable
    // expressions, not of the wrong type — both reference implementations
    // report it that way.
    [InlineData("2 #<==> (_X #= 1)", "2")]
    [InlineData("foo #<==> (_X #= 1)", "foo")]
    public void ANonReifiableValueIsADomainError(string goal, string culprit)
    {
        var e = Clpfd();
        Assert.True(e.Query(
            $"catch(({goal}), error(E, _), true), "
            + $"E = domain_error(clpfd_reifiable_expression, {culprit}).").Success);
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Domains written as unions (X in 1\/3\/5..6), the reflection
/// predicates fd_var/1, fd_inf/2, fd_sup/2, fd_size/2 and fd_dom/2, and how
/// a domain is written back. The expected values are what Scryer's clpz
/// answers for the same goals.</summary>
public sealed class ClpfdDomainsAndReflectionTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        return e;
    }

    private static void Holds(string goal)
        => Assert.True(Engine().Query(goal + ".").Success, goal);

    [Theory]
    [InlineData(@"\+ fd_var(3)")]
    [InlineData(@"\+ fd_var(_)")]
    [InlineData("X in 1..3, fd_var(X)")]
    [InlineData("fd_inf(3, I), I == 3")]
    [InlineData("fd_inf(_, I), I == inf")]
    [InlineData("X in 2..8, fd_inf(X, I), I == 2")]
    [InlineData("X #> 2, fd_sup(X, S), S == sup")]
    [InlineData("X in 2..8, fd_sup(X, S), S == 8")]
    [InlineData("fd_size(5, S), S == 1")]
    [InlineData(@"X in 1..3 \/ 7..9, fd_size(X, S), S == 6")]
    [InlineData("X #> 2, fd_size(X, S), S == sup")]
    [InlineData("fd_size(_, S), S == sup")]
    [InlineData("fd_dom(5, D), D == 5..5")]
    [InlineData(@"X in 1..3 \/ 5 \/ 7..9, fd_dom(X, D), D == 1..3 \/ 5 \/ 7..9")]
    [InlineData("fd_dom(_, D), D == inf..sup")]
    [InlineData("X #> 2, fd_dom(X, D), D == 3..sup")]
    public void Reflection(string goal) => Holds(goal);

    [Theory]
    [InlineData(@"X in 1 \/ 3 \/ 5..6, fd_dom(X, D), D == 1 \/ 3 \/ 5..6")]
    [InlineData(@"X in inf..3 \/ 10..sup, fd_dom(X, D), D == inf..3 \/ 10..sup")]
    [InlineData(@"X in 1..5 \/ 3..9, fd_dom(X, D), D == 1..9")]
    [InlineData(@"[X, Y] ins 1 \/ 4, fd_dom(X, D), D == 1 \/ 4, fd_dom(Y, D)")]
    [InlineData(@"X in 1 \/ 3, Y in 1 \/ 3, Z in 1..3, all_distinct([X, Y, Z]), X = 1, Y == 3")]
    [InlineData(@"\+ (_ in 3..1)")]
    [InlineData(@"\+ (X in 1 \/ 3, X = 2)")]
    public void Unions(string goal) => Holds(goal);

    [Theory]
    [InlineData(@"X in 1..9, X #\= 4, X #\= 6, X #\= 7", @"X in 1..3 \/ 5 \/ 8..9")]
    [InlineData(@"X in 1..9, X #\= 5", @"X in 1..4 \/ 6..9")]
    public void ADomainIsWrittenAsInReadsIt(string goal, string residue)
        => Holds($"{goal}, copy_term(X, X, [G]), G == ({residue})");

    [Theory]
    [InlineData("_ in foo", "foo")]
    [InlineData("_ in a..3", "a..3")]
    [InlineData("_ in 1..b", "1..b")]
    [InlineData("_ in 1.5..3", "1.5..3")]
    [InlineData("_ in 3..inf", "3..inf")]
    [InlineData("_ in sup..3", "sup..3")]
    [InlineData(@"_ in (1..3) \/ foo", @"1..3 \/ foo")]
    [InlineData("[_, _] ins a..3", "a..3")]
    public void AMalformedDomainIsADomainError(string goal, string domain)
        => Holds($"catch(({goal}), error(domain_error(clpfd_domain, D), C), true), D == ({domain}), C == (in)/2");

    [Theory]
    [InlineData("_ in _..3")]
    [InlineData(@"_ in 1 \/ _")]
    public void AVariableWhereAValueBelongsIsAnInstantiationError(string goal)
        => Holds($"catch(({goal}), error(instantiation_error, _), true)");

    [Fact]
    public void AnythingElseIsATypeErrorForTheReflectionPredicates()
        => Holds("catch(fd_inf(a, _), error(type_error(integer, a), _), true)");
}

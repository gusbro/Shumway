using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>length/2 over a proper list is one spine walk: the fast path
/// runs first and the cycle check only once it has failed (counted by
/// LengthSingleWalkDiagTests, on the diagnostic lane). This pins every
/// mode and error of the predicate so the reordering keeps them.</summary>
public class LengthSingleWalkTests
{
    private static bool Holds(string query) => new PrologEngine().Query(query).Success;

    [Theory]
    [InlineData("length([a, b, c], N), N == 3.")]
    [InlineData("length([a, b, c], 3).")]
    [InlineData("\\+ length([a, b], 3).")]
    [InlineData("length([], 0), length([], N), N == 0.")]
    [InlineData("length(\"abc\", N), N == 3.")]
    [InlineData("length(L, 2), L = [_, _].")]
    [InlineData("length(L, 0), L == [].")]
    [InlineData("length([a|T], 3), T = [_, _].")]
    [InlineData("findall(N, (length([a|_], N), (N >= 3 -> ! ; true)), Ns), Ns == [1, 2, 3].")]
    [InlineData("findall(L, (length(L, N), (N >= 2 -> ! ; true)), Ls), length(Ls, 3).")]
    public void ProperAndPartialListModes(string query) => Assert.True(Holds(query));

    [Theory]
    // Neumerkel's length cases 4-7: an improper tail fails, no type_error.
    [InlineData("\\+ length([a|b], _).")]
    [InlineData("\\+ length([a|b], 1).")]
    [InlineData("\\+ length(foo, _).")]
    [InlineData("\\+ length(foo, 0).")]
    // A cyclic list against a concrete length is false (length#27).
    [InlineData("L = [a, b|L], \\+ length(L, 3).")]
    [InlineData("L = [a, b|L], \\+ length(L, 0).")]
    public void ImproperAndCyclicListsFail(string query) => Assert.True(Holds(query));

    [Theory]
    [InlineData("catch(length([a, b], -1), error(domain_error(not_less_than_zero, -1), length/2), true).")]
    [InlineData("catch(length(_, -1), error(domain_error(not_less_than_zero, -1), length/2), true).")]
    [InlineData("catch(length([a, b], foo), error(type_error(integer, foo), length/2), true).")]
    [InlineData("catch(length([a, b], 2.0), error(type_error(integer, 2.0), length/2), true).")]
    [InlineData("catch(length(_, foo), error(type_error(integer, foo), length/2), true).")]
    [InlineData("catch(length([a|_], foo), error(type_error(integer, foo), length/2), true).")]
    // A cyclic list with the length unconstrained (length#26), or the list
    // being its own length: no finite memory holds the answer.
    [InlineData("L = [a, b|L], catch(length(L, _), error(resource_error(finite_memory), length/2), true).")]
    [InlineData("L = [a, b|L], catch(length(L, foo), error(type_error(integer, foo), length/2), true).")]
    [InlineData("catch(length(L, L), error(resource_error(finite_memory), length/2), true).")]
    [InlineData("catch(length([a|L], L), error(resource_error(finite_memory), length/2), true).")]
    public void TheErrorsOfEveryMode(string query) => Assert.True(Holds(query));
}

using Shumway.Builtins;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>length/2 over a proper list is one spine walk: the fast path
/// runs first and the cycle check only once it has failed. Counted, not
/// timed: the diagnostic counter of <c>'$cyclic_spine'/1</c>.</summary>
public sealed class LengthSingleWalkDiagTests
{
    [DiagFact]
    public void AProperListIsWalkedOnce_NoCycleCheck()
    {
        var e = new PrologEngine();
        long before = MultiSolutionHelpers.CyclicSpineCalls;
        Assert.True(e.Query(
            "findall(X, between(1, 20000, X), L), length(L, N), N == 20000.").Success);
        Assert.Equal(0, MultiSolutionHelpers.CyclicSpineCalls - before);
        // Against a bound length, and a packed list (ADR-047): the same path.
        before = MultiSolutionHelpers.CyclicSpineCalls;
        Assert.True(e.Query(
            "findall(X, between(1, 20000, X), L), length(L, 20000), length(\"abcdef\", 6).").Success);
        Assert.Equal(0, MultiSolutionHelpers.CyclicSpineCalls - before);
    }

    [DiagFact]
    public void ACyclicListStillGetsTheCycleCheck()
    {
        var e = new PrologEngine();
        long before = MultiSolutionHelpers.CyclicSpineCalls;
        Assert.True(e.Query("L = [a, b|L], \\+ length(L, 3).").Success);
        Assert.Equal(1, MultiSolutionHelpers.CyclicSpineCalls - before);
        before = MultiSolutionHelpers.CyclicSpineCalls;
        Assert.True(e.Query(
            "L = [a, b|L], catch(length(L, _), error(resource_error(finite_memory), length/2), true).").Success);
        Assert.Equal(1, MultiSolutionHelpers.CyclicSpineCalls - before);
    }
}

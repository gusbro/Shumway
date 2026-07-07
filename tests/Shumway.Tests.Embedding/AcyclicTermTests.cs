using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 (Logtalk bring-up): <c>acyclic_term/1</c> — true iff the term is
/// finite (no rational/cyclic structure). Needed by the Logtalk <c>term</c>
/// library object.
/// </summary>
public class AcyclicTermTests
{
    private static bool Holds(string goal) => new PrologEngine().Query(goal).Success;

    [Fact] public void Compound_IsAcyclic() => Assert.True(Holds("acyclic_term(f(a, b))."));
    [Fact] public void List_IsAcyclic() => Assert.True(Holds("acyclic_term([1, 2, 3])."));
    [Fact] public void Atomic_IsAcyclic() => Assert.True(Holds("acyclic_term(hello)."));
    [Fact] public void Unbound_IsAcyclic() => Assert.True(Holds("acyclic_term(_)."));

    [Fact] public void SharedDag_IsAcyclic() =>
        // Shared (non-cyclic) subterm must NOT be mistaken for a cycle.
        Assert.True(Holds("S = shared, acyclic_term(pair(S, S))."));

    [Fact] public void SelfReferentialCompound_IsCyclic() =>
        Assert.False(Holds("X = f(X), acyclic_term(X)."));

    [Fact] public void CyclicList_IsCyclic() =>
        Assert.False(Holds("X = [a|X], acyclic_term(X)."));

    [Fact] public void NestedCyclic_IsCyclic() =>
        Assert.False(Holds("X = g(x, h(X)), acyclic_term(X)."));
}

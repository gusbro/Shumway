using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A compiled bagof/setof whose witness names may be bound at the call
/// computes its witness with <c>'$bagof_free'</c> before the goal runs; inside
/// the module that call is a builtin like any other, and the groups come out
/// as on the other tiers.</summary>
public sealed class BagofFreeWitnessTests
{
    [Fact]
    public void TheWitnessComputedAtTheCallGroupsAsOnTheOtherTiers()
    {
        var (engine, members) = TieredEngine.Build("""
            foo(1, a). foo(2, b). foo(3, c). foo(4, d).
            baz(1, g(a)). baz(2, g(b)). baz(3, g(a)).
            call_goal(L) :- G = foo(X, Y), bagof(X, Y^call(G), L).
            bound_witness(S) :- Z = g(Y), setof(X, Y^baz(X, Z), S).
            grouped(Z-L) :- q(Z), bagof(X, baz(X, Z), L).
            q(Z) :- Z = g(_).
            """);

        Assert.True(engine.Query("findall(R, call_goal(R), Rs), Rs == [[1,2,3,4]].").Success);
        Assert.True(engine.Query("findall(R, bound_witness(R), Rs), Rs == [[1,2,3]].").Success);
        Assert.True(engine.Query("findall(R, grouped(R), Rs), Rs == [g(a)-[1,3],g(b)-[2]].").Success);
        var promoted = members.Select(m => FunctorTable.Lookup(m.Predicate.FunctorId))
            .Select(f => $"{AtomTable.GetById(f.AtomId)?.Name}/{f.Arity}").ToList();
        foreach (string p in new[] { "$call_goal/1", "$bound_witness/1", "$grouped/1" })
            Assert.True(promoted.Exists(n => n.EndsWith(p)), $"{p} did not promote to the module");
    }
}

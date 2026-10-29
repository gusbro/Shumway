using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The free variables of a compiled bagof/setof are those of the goal
/// as instantiated when it is called, not as written. A variable bound before
/// the call to a term holding the template or a ^-quantified variable must
/// not split the solutions into groups, and a goal variable bound to
/// <c>V^G</c> quantifies V. The expected answers are SICStus Prolog's.</summary>
public class BagofBoundWitnessTests
{
    private const string Program = @"
        foo(1, a). foo(2, b). foo(3, c). foo(4, d).
        baz(1, g(a)). baz(2, g(b)). baz(3, g(a)).
        w(G) :- G.
        var_goal(L) :- G = foo(X, Y), bagof(X, Y^G, L).
        var_goal_set(S) :- G = foo(X, Y), setof(X, Y^G, S).
        call_goal(L) :- G = foo(X, Y), bagof(X, Y^call(G), L).
        wrapped_goal(L) :- G = foo(X, Y), bagof(X, Y^w(G), L).
        bound_witness(L) :- Z = g(Y), bagof(X, Y^baz(X, Z), L).
        bound_witness_set(S) :- Z = g(Y), setof(X, Y^baz(X, Z), S).
        nested_caret(L) :- G = (Y^foo(X, Y)), bagof(X, Z^G, L), var(Z).
        grouped(Y-L) :- bagof(X, foo(X, Y), L).
        grouped_head(Y, L) :- bagof(X, foo(X, Y), L).
        grouped_after(K-L) :- bagof(X, (foo(X, Y), K = k(Y)), L).
        grouped_bound(Z-L) :- Z = g(_), bagof(X, baz(X, Z), L).
        ";

    private static PrologEngine Engine()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Program);
        return engine;
    }

    [Theory]
    [InlineData(@"var_goal(R)", @"[[1,2,3,4]]")]
    [InlineData(@"var_goal_set(R)", @"[[1,2,3,4]]")]
    [InlineData(@"call_goal(R)", @"[[1,2,3,4]]")]
    [InlineData(@"wrapped_goal(R)", @"[[1,2,3,4]]")]
    [InlineData(@"bound_witness(R)", @"[[1,2,3]]")]
    [InlineData(@"bound_witness_set(R)", @"[[1,2,3]]")]
    [InlineData(@"nested_caret(R)", @"[[1,2,3,4]]")]
    public void AVariableBoundBeforeTheCallDoesNotSplitTheGroups(string goal, string expected)
    {
        Assert.True(Engine().Query($"findall(R, {goal}, Rs), Rs == {expected}.").Success, goal);
    }

    [Theory]
    [InlineData(@"grouped(R)", @"[a-[1],b-[2],c-[3],d-[4]]")]
    [InlineData(@"(grouped_head(Y, L), R = Y-L)", @"[a-[1],b-[2],c-[3],d-[4]]")]
    [InlineData(@"grouped_after(R)", @"[k(a)-[1],k(b)-[2],k(c)-[3],k(d)-[4]]")]
    [InlineData(@"grouped_bound(R)", @"[g(a)-[1,3],g(b)-[2]]")]
    public void AFreeVariableStillGroupsTheSolutions(string goal, string expected)
    {
        Assert.True(Engine().Query($"findall(R, {goal}, Rs), Rs == {expected}.").Success, goal);
    }
}

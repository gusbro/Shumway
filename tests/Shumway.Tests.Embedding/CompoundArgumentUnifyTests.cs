using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Unification of two compounds settles the usual argument pairs
/// in place and hands the rest to the general unifier. Each case here is a
/// pair kind on one side of that split, inside a compound on both tiers.</summary>
public sealed class CompoundArgumentUnifyTests
{
    private const string Program = """
        same(X, Y) :- X = Y.
        """;

    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        e.IlPromotion.Threshold = threshold;
        return e;
    }

    [Theory]
    // Atoms and small integers, equal and not.
    [InlineData(@"same(f(a, 1), f(a, 1))", true)]
    [InlineData(@"same(f(a, 1), f(b, 1))", false)]
    [InlineData(@"same(f(a, 1), f(a, 2))", false)]
    [InlineData(@"same(f(a), f(1))", false)]
    // A variable on either side, bound to an atom, a compound, a list, a string.
    [InlineData(@"same(f(X, Y), f(a, g(Z))), X == a, Y = g(W), W == Z", true)]
    [InlineData(@"same(f(a, [1|T]), f(X, Y)), X == a, Y = [1|T2], T2 == T", true)]
    [InlineData(@"S = ""abc"", same(f(S), f(X)), X == ""abc""", true)]
    // Two variables: the younger is bound to the older, and undone on backtracking.
    [InlineData(@"( same(f(X, Y), f(Y, X)), X == Y, fail ; true ), var(X), var(Y), X \== Y", true)]
    [InlineData(@"( same(f(X), f(a)), fail ; true ), var(X)", true)]
    // Off-cell numbers.
    [InlineData(@"same(f(1.5, 100000000000000000000), f(1.5, 100000000000000000000))", true)]
    [InlineData(@"same(f(1.5), f(2.5))", false)]
    [InlineData(@"same(f(100000000000000000000), f(100000000000000000001))", false)]
    // A partial string against a list, both ways.
    [InlineData(@"atom_codes(abc, Cs), same(f(Cs), f([0'a|T])), T == [0'b, 0'c]", true)]
    [InlineData(@"atom_chars(ab, Cs), same(f([a, b]), f(Cs))", true)]
    // Nested compounds and rational trees.
    [InlineData(@"same(f(g(h(a)), b), f(g(h(X)), Y)), X == a, Y == b", true)]
    [InlineData(@"X = f(X, a), Y = f(Y, a), same(g(X), g(Y))", true)]
    public void ArgumentPairs(string goal, bool expected)
    {
        foreach (int threshold in new[] { 0, 1 })
        {
            var e = Engine(threshold);
            for (int i = 0; i < 3; i++)
                Assert.Equal(expected, e.Query(goal + ".").Success);
        }
    }

    /// <summary>An attributed variable in an argument goes through its hook.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AnAttributedArgumentRunsItsHook(int threshold)
    {
        var e = Engine(threshold);
        e.ConsultString("""
            :- public verify_attributes/4.
            verify_attributes(dom, Allowed, Value, []) :- memberchk(Value, Allowed).
            """);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query("put_attr(X, dom, [a, b]), same(f(X, 1), f(a, 1)).").Success);
            Assert.False(e.Query("put_attr(X, dom, [a, b]), same(f(X, 1), f(c, 1)).").Success);
            // A plain variable meeting it is bound to it: binding the plain
            // one later binds the attributed one, through the hook.
            Assert.True(e.Query("put_attr(X, dom, [a, b]), same(f(1, Y), f(1, X)), Y = b, X == b.").Success);
            Assert.True(e.Query("put_attr(X, dom, [a, b]), same(f(1, X), f(1, Y)), Y = b, X == b.").Success);
            Assert.False(e.Query("put_attr(X, dom, [a, b]), same(f(1, Y), f(1, X)), Y = c.").Success);
            Assert.False(e.Query("put_attr(X, dom, [a, b]), same(f(1, X), f(1, Y)), Y = c.").Success);
        }
    }
}

using Shumway.Compiler.Parsing;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A predicate split at the changes of its head constants
/// (<see cref="KeySplit"/>) answers what the predicate as written answers, in
/// the same order and leaving the same choice points. Each test checks that
/// the split it is about happened, or that it did not.</summary>
// Sets the split's weight, a process static: it runs alone, in the gate's
// exclusive pass.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class KeySplitTests
{
    // Three runs by the second argument, a clause on its own between them, and
    // a first argument that is the same structure in every clause.
    private const string Shapes = @"
        shape(P-Q, circle, A) :- A is P * Q.
        shape(P-_, circle, A) :- A is P + 100.
        shape(P-Q, square, A) :- A is P - Q.
        shape(_-Q, square, A) :- A is Q + 200.
        shape(P-Q, square, A) :- A is P * 10 + Q.
        shape(P-Q, Other, A) :- atom(Other), A is P + Q + 1000.
        shape(P-Q, line, A) :- A is P + Q.
        shape(P-_, line, P).
    ";

    private static PrologEngine Engine(string program, int minWeight, int threshold = 0)
    {
        int before = KeySplit.MinWeight;
        try
        {
            KeySplit.MinWeight = minWeight;
            var e = new PrologEngine();
            e.IlPromotion.Threshold = threshold;
            e.IlPromotion.BackgroundCompilation = false;
            e.ConsultString(program);
            // The program is built at the first query, under the weight set here.
            Assert.True(e.Query("true.").Success);
            return e;
        }
        finally
        {
            KeySplit.MinWeight = before;
        }
    }

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query($"findall(T, ({goal}), L), term_to_atom(L, Text).");
        Assert.True(r.Success, goal);
        return r["Text"]!.ToString()!;
    }

    // Called with fresh arguments, a run's predicate may raise from its body:
    // only an existence error says it is not there.
    private static bool Defined(PrologEngine e, string name, int arity)
    {
        string args = string.Join(",", Enumerable.Repeat("_", arity));
        return e.Query(
            $"catch(('{name}'({args}) ; true), E, (E = error(existence_error(_, _), _) -> fail ; true)).").Success;
    }

    [Theory]
    [InlineData(@"shape(3-4, circle, T)")]
    [InlineData(@"shape(3-4, square, T)")]
    [InlineData(@"shape(3-4, line, T)")]
    [InlineData(@"shape(3-4, hexagon, T)")]
    [InlineData(@"shape(3-4, K, A), T = K-A")]
    [InlineData(@"shape(3-4, K, 7), T = K")]
    [InlineData(@"shape(3-4, circle, 12), T = yes")]
    [InlineData(@"shape(nothing, circle, T)")]
    public void A_split_predicate_answers_as_written(string goal)
    {
        string expected = Answer(Engine(Shapes, 0), goal);
        var split = Engine(Shapes, 1);
        Assert.True(Defined(split, "$key1_shape", 3), "shape/3 was not split");
        Assert.Equal(expected, Answer(split, goal));
    }

    [Theory]
    [InlineData(@"shape(3-4, circle, T)")]
    [InlineData(@"shape(3-4, K, A), T = K-A")]
    [InlineData(@"shape(3-4, line, T)")]
    public void A_split_predicate_answers_the_same_on_tier_1(string goal)
    {
        string expected = Answer(Engine(Shapes, 0), goal);
        var split = Engine(Shapes, 1, threshold: 1);
        Assert.True(Defined(split, "$key1_shape", 3), "shape/3 was not split");
        Assert.Equal(expected, Answer(split, goal));   // compiles
        Assert.Equal(expected, Answer(split, goal));   // runs compiled
    }

    // The last answer of a run leaves no choice point when the runs after it
    // carry another constant: the dispatching clause holds the run's.
    [Theory]
    [InlineData(@"shape(3-4, circle, 103)")]
    [InlineData(@"shape(3-4, square, 34)")]
    [InlineData(@"shape(3-4, line, 3)")]
    public void A_run_is_left_as_the_predicate_is(string goal)
    {
        string probe = $"setup_call_cleanup(true, ({goal}), Det = yes), T = Det";
        string expected = Answer(Engine(Shapes, 0), probe);
        var split = Engine(Shapes, 1);
        Assert.True(Defined(split, "$key1_shape", 3), "shape/3 was not split");
        Assert.Equal(expected, Answer(split, probe));
    }

    [Fact]
    public void A_clause_alone_in_its_run_stays_in_the_predicate()
    {
        var split = Engine(Shapes, 1);
        Assert.True(Defined(split, "$key3_shape", 3), "three runs expected");
        Assert.False(Defined(split, "$key4_shape", 3), "the clause on its own took a predicate");
    }

    // A cut in a run's predicate would commit the run only.
    [Theory]
    [InlineData(@"
        pick(a, 1) :- !.
        pick(a, 2).
        pick(b, 3).
        pick(b, 4).
        pick(_, 5).", @"pick(a, T)")]
    [InlineData(@"
        pick(a, X) :- ( X = 1 ; X = 2, ! ).
        pick(a, 3).
        pick(b, 4).
        pick(b, 5).", @"pick(a, T)")]
    public void A_predicate_with_a_cut_is_not_split(string program, string goal)
    {
        string expected = Answer(Engine(program, 0), goal);
        var e = Engine(program, 1);
        Assert.False(Defined(e, "$key1_pick", 2), "pick/2 was split");
        Assert.Equal(expected, Answer(e, goal));
    }

    // The dispatching clause holds a variable where the heads hold structures:
    // it could not tell f from g, and the predicate's own index can.
    [Fact]
    public void A_predicate_whose_structures_differ_is_not_split()
    {
        const string program = @"
            wrap(f(X), a, X).
            wrap(f(X), a, Y) :- Y is X + 1.
            wrap(g(X), b, X).
            wrap(g(X), b, Y) :- Y is X + 2.";
        var e = Engine(program, 1);
        Assert.False(Defined(e, "$key1_wrap", 3), "wrap/3 was split");
        Assert.Equal("[1,2]", Answer(e, "wrap(f(1), _, T)"));
    }

    [Fact]
    public void A_predicate_under_the_weight_is_not_split()
    {
        var e = Engine(Shapes, 100_000);
        Assert.False(Defined(e, "$key1_shape", 3), "shape/3 was split");
        Assert.Equal("[12,103,1007]", Answer(e, "shape(3-4, circle, T)"));
    }

    // Clauses consulted later join the predicate: the runs are those of the
    // predicate as it stands.
    [Fact]
    public void Clauses_added_by_a_second_consult_are_answered_in_order()
    {
        const string more = @"
            shape(P-Q, line, A) :- A is P - Q - 1.
            shape(P-Q, dot, A) :- A is P * Q * 2.
            shape(P-Q, dot, A) :- A is P * Q * 3.";
        string Run(int weight)
        {
            int before = KeySplit.MinWeight;
            try
            {
                KeySplit.MinWeight = weight;
                var e = new PrologEngine();
                e.ConsultString(Shapes);
                Assert.True(e.Query("true.").Success);
                e.ConsultString(more);
                return Answer(e, "shape(3-4, K, A), T = K-A");
            }
            finally
            {
                KeySplit.MinWeight = before;
            }
        }
        Assert.Equal(Run(0), Run(1));
    }
}

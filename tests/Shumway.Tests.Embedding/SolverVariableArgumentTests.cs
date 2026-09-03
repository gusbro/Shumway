using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The same defect issue #76 reported for frozen goals, swept
/// through the constraint libraries: an argument the library inspects by
/// SHAPE — a list, a relation, a domain, a reified goal, a constraint —
/// reaching a clause head that matches a pattern, and being BOUND by it.
///
/// <para>Three symptoms, all present before: a silent wrong answer
/// (label(V) bound V to [] and succeeded, sum([1,2], R, T) picked the
/// relation for the caller), a type_error about a variable (X in D), and a
/// hang ({C} in CLP(R) recursed on fresh halves forever). Every entry point
/// now rejects a variable where it needs a value, naming itself in the
/// error; a bound-but-wrong value keeps the error it always had.</para></summary>
public class SolverVariableArgumentTests
{
    private static PrologEngine WithLibrary(string name)
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query($"use_module(library({name})).").Success);
        return e;
    }

    /// <summary>Runs <paramref name="goal"/> bounded (one of these looped
    /// forever) and returns the ISO error kind it raised.</summary>
    private static string ErrorKindOf(string library, string goal)
    {
        string kind = "";
        Exception? failure = null;
        var t = new System.Threading.Thread(() =>
        {
            try
            {
                var e = WithLibrary(library);
                var sol = e.Query($"catch(({goal}), error(E, _), true).");
                Assert.True(sol.Success);
                kind = sol["E"]?.ToString() ?? "<no error>";
            }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)),
            $"`{goal}` did not terminate — the walk is binding and recurring again");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException($"{failure.GetType().Name}: {failure.Message}");
        return kind;
    }

    [Theory]
    // Lists the solver walks: binding one to [] answered "yes, nothing to do".
    [InlineData("label(V)")]
    [InlineData("labeling(Opts, [])")]
    [InlineData("labeling([ff|Rest], [])")]
    [InlineData("all_distinct(L)")]
    [InlineData("all_different(L)")]
    [InlineData("sum(L, #=, 0)")]
    [InlineData("scalar_product(Cs, Vs, #=, 0)")]
    [InlineData("Vs ins 1..3")]
    [InlineData("fd_all_different(L)")]
    [InlineData("fd_atmost(N, L, 1)")]
    [InlineData("fd_exactly(N, L, 1)")]
    [InlineData("fd_only_one(L)")]
    [InlineData("fd_at_most_one(L)")]
    // A relation is a value, not something to choose for the caller.
    [InlineData("sum([1,2], R, T)")]
    [InlineData("scalar_product([1], [X], R, T)")]
    // A domain, likewise — this one reported type_error(fd_bound, _),
    // about a variable, which no type_error ever is.
    [InlineData("X in D")]
    [InlineData("fd_domain(X, L, H)")]
    // A reified constraint is a goal: inventing `_A #= _B` is not it.
    [InlineData("B #<==> C")]
    [InlineData("B #==> C")]
    [InlineData("#\\ C")]
    // An unconstrained variable cannot be enumerated.
    [InlineData("indomain(V)")]
    [InlineData("fd_labeling(V)")]
    [InlineData("fd_set_vector_max(M)")]
    public void ClpfdRejectsAVariableWhereItNeedsAValue(string goal)
        => Assert.Equal("instantiation_error", ErrorKindOf("clpfd", goal));

    [Theory]
    [InlineData("{C}")]                       // looped forever
    [InlineData("{X > 1, C}")]
    [InlineData("entailed(C)")]               // looped forever
    [InlineData("{X =:= 1}, entailed((X =:= 1, C))")]
    public void ClprRejectsAVariableConstraint(string goal)
        => Assert.Equal("instantiation_error", ErrorKindOf("clpr", goal));

    [Theory]
    // A bound-but-wrong value keeps the exact error it always raised: the
    // guards are about instantiation, and change nothing else.
    [InlineData("sum([_], foo, _)", "domain_error(clpfd_relation, foo)")]
    [InlineData("scalar_product([1], [_], bogus, _)", "domain_error(clpfd_relation, bogus)")]
    [InlineData("labeling([bogus], [])", "domain_error(labeling_option, bogus)")]
    [InlineData("_X in foo", "type_error(fd_domain, foo)")]
    public void ABoundButWrongValueKeepsItsError(string goal, string expected)
        => Assert.Equal(expected, ErrorKindOf("clpfd", goal));

    [Theory]
    // The solvers still solve.
    [InlineData("clpfd", "X in 1..3, Y in 1..3, X #\\= Y, label([X,Y]), X == 1, Y == 2")]
    [InlineData("clpfd", "sum([X,Y], #=, 5), [X,Y] ins 0..5, label([X,Y]), X == 0, Y == 5")]
    [InlineData("clpfd", "scalar_product([2,3],[X,Y],#=,12), [X,Y] ins 0..4, label([X,Y])")]
    [InlineData("clpfd", "X in 1..2, B #<==> (X #= 1), label([X,B]), X == 1, B == 1")]
    [InlineData("clpfd", "all_distinct([X,Y]), [X,Y] ins 1..2, label([X,Y])")]
    [InlineData("clpfd", "X in 1..3, indomain(X), X == 1")]
    [InlineData("clpfd", "fd_domain([X,Y], 1, 3), fd_labeling([X,Y])")]
    [InlineData("clpfd", "fd_domain([X,Y],0,1), fd_exactly(1,[X,Y],1), fd_labeling([X,Y])")]
    [InlineData("clpr", "{X > 1, X < 3}")]
    [InlineData("clpr", "{X =:= 2}, entailed(X =:= 2)")]
    public void TheSolversStillSolve(string library, string goal)
    {
        var e = WithLibrary(library);
        Assert.True(e.Query(goal + ".").Success);
    }
}

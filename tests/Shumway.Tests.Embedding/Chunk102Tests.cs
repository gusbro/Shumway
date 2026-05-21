using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 102 (Phase 7): CLP(R) constraint projection. <c>copy_term/3</c>
/// collects the residual constraints on the copied term's variables,
/// re-expressed over the copy as <c>{...}</c> goals — so they can be
/// inspected or re-posted.
/// </summary>
public class Chunk102Tests
{
    private static bool Holds(string query)
    {
        var engine = new PrologEngine();
        engine.UseClpr();
        return engine.Query(query).Success;
    }

    [Fact]
    public void NoConstraints_ProjectEmpty()
    {
        Assert.True(Holds("copy_term(foo(a, b), _, Goals), Goals == []."));
    }

    [Fact]
    public void DeterminedVariable_HasNoResidual()
    {
        // {X =:= 5} binds X to 5 outright — the copy carries no attvar.
        Assert.True(Holds("{X =:= 5}, copy_term(X, _, Goals), Goals == []."));
    }

    [Fact]
    public void SolvedVariable_ProjectsItsLinearForm()
    {
        // X = 2Y + 1 projects as a goal that, re-posted, reconstructs the
        // relation: pinning the copy of Y to 4 then gives the copy of X 9.
        Assert.True(Holds(
            "{X =:= 2*Y + 1}, copy_term(X-Y, Cx-Cy, Goals), " +
            "maplist(call, Goals), {Cy =:= 4, Cx =:= 9}."));
        Assert.False(Holds(
            "{X =:= 2*Y + 1}, copy_term(X-Y, Cx-Cy, Goals), " +
            "maplist(call, Goals), {Cy =:= 4, Cx =:= 8}."));
    }

    [Fact]
    public void Inequality_ProjectsAndRoundTrips()
    {
        Assert.True(Holds(
            "{X + Y >= 10}, copy_term(X-Y, Cx-Cy, Goals), " +
            "maplist(call, Goals), {Cx =:= 3, Cy =:= 8}."));
        Assert.False(Holds(
            "{X + Y >= 10}, copy_term(X-Y, Cx-Cy, Goals), " +
            "maplist(call, Goals), {Cx =:= 3, Cy =:= 3}."));
    }

    [Fact]
    public void SharedConstraint_ProjectedOnlyOnce()
    {
        // X + Y >= 10 is stored on both X and Y but projected by one of
        // them only — Goals holds a single goal, not a duplicate pair.
        Assert.True(Holds(
            "{X + Y >= 10}, copy_term(X-Y, _, Goals), length(Goals, N), N =:= 1."));
    }

    [Fact]
    public void Disequality_ProjectsAndRoundTrips()
    {
        Assert.True(Holds(
            "{X =\\= Y}, copy_term(X-Y, Cx-Cy, Goals), " +
            "maplist(call, Goals), {Cx =:= 5, Cy =:= 6}."));
        Assert.False(Holds(
            "{X =\\= Y}, copy_term(X-Y, Cx-Cy, Goals), " +
            "maplist(call, Goals), {Cx =:= 5, Cy =:= 5}."));
    }

    [Fact]
    public void NonLinearConstraint_ProjectsAndRoundTrips()
    {
        Assert.True(Holds(
            "{X * Y =:= 6}, copy_term(X-Y, Cx-Cy, Goals), " +
            "maplist(call, Goals), {Cx =:= 2}, {Cy =:= 3}."));
        Assert.False(Holds(
            "{X * Y =:= 6}, copy_term(X-Y, Cx-Cy, Goals), " +
            "maplist(call, Goals), {Cx =:= 2}, {Cy =:= 4}."));
    }
}

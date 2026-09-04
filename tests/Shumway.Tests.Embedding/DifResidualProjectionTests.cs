using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What a residual <c>dif/2</c> is SHOWN as. The store was already
/// right in every case here — each test therefore checks the answer text AND
/// that the constraint still decides the same goals, because a projection
/// that quietly weakened one would be far worse than a verbose one.</summary>
public class DifResidualProjectionTests
{
    private static PrologEngine Coroutining()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query("use_module(library(coroutining)).").Success);
        return e;
    }

    private static string AnswerOf(string goal)
    {
        var session = new TopLevelSession(Coroutining());
        using var run = session.StartQuery(goal);
        Assert.True(run.MoveNext());
        return run.Format(400);
    }

    [Theory]
    // A constraint another one already forbids adds nothing to the answer.
    // Equating g(A,B) with g(C,C) forces A and B together, which dif(A, B)
    // already rules out — so only the stronger one is shown, whichever was
    // posted first.
    [InlineData("dif(A, B), dif(g(A, B), g(C, C)).")]
    [InlineData("dif(g(A, B), g(C, C)), dif(A, B).")]
    public void ASubsumedConstraintIsNotShown(string goal)
        => Assert.Equal("dif(A, B)", AnswerOf(goal));

    [Fact]
    public void TheSubsumedConstraintIsStillEnforced()
    {
        // What is not shown is still true: the answer got shorter, not weaker.
        var e = Coroutining();
        Assert.False(e.Query("dif(A, B), dif(g(A, B), g(C, C)), A = C, B = C.").Success);
        Assert.False(e.Query("dif(g(A, B), g(C, C)), dif(A, B), A = C, B = C.").Success);
        // ...and a valuation both constraints allow still succeeds.
        Assert.True(e.Query(
            "dif(A, B), dif(g(A, B), g(C, C)), A = 1, B = 2, C = 3.").Success);
    }

    [Fact]
    public void AliasingTwoWatchedVariablesCollapsesTheDisjunction()
    {
        // dif(p(U,M), p(V,N)) forbids U = V AND M = N together. Once M and N
        // are the same variable, only U \= V is left to forbid, so that is
        // what the answer says. Nothing invented: both are subterms of what
        // was written.
        string answer = AnswerOf("dif(p(U, M), p(V, N)), M = N.");
        Assert.Contains("dif(U, V)", answer);
        Assert.DoesNotContain("dif(p(", answer);
    }

    [Fact]
    public void TheCollapsedConstraintDecidesTheSameGoals()
    {
        var e = Coroutining();
        Assert.False(e.Query("dif(p(U, M), p(V, N)), M = N, U = V.").Success);
        Assert.False(e.Query("dif(p(U, M), p(V, N)), U = V, M = N.").Success);
        Assert.True(e.Query("dif(p(U, M), p(V, N)), M = N, U = 1, V = 2.").Success);
    }

    [Theory]
    // A genuine disjunction is left alone: dif(p(U,M), p(V,N)) is "U \= V or
    // M \= N", and neither half may be shown on its own. Independent
    // constraints likewise stay side by side.
    [InlineData("dif(p(U, M), p(V, N)).", "dif(p(U, M), p(V, N))")]
    [InlineData("dif(A, B), dif(C, D).", "dif(A, B),\ndif(C, D)")]
    [InlineData("dif(A, B), dif(A, C).", "dif(A, B),\ndif(A, C)")]
    public void WhatMustNotBeSimplifiedIsNot(string goal, string expected)
        => Assert.Equal(expected, AnswerOf(goal));

    [Theory]
    // Already true before this change; here so the pass is pinned not to
    // undo them. A one-pair unifier IS a single disequality, whichever way
    // it was written.
    [InlineData("dif(s(A), s(B)).", "dif(A, B)")]
    [InlineData("dif(A, B), dif(B, A).", "dif(A, B)")]
    public void TheCanonicalFormsStayCanonical(string goal, string expected)
        => Assert.Equal(expected, AnswerOf(goal));

    [Fact]
    public void ProjectionDoesNotDisturbAFrozenGoal()
    {
        // freeze/2 residuals travel the same projection and must come out
        // untouched — the pass only ever looks at dif/2 goals.
        string answer = AnswerOf("freeze(X, true), dif(A, B).");
        Assert.Contains("freeze(X, true)", answer);
        Assert.Contains("dif(A, B)", answer);
    }

    [Fact]
    public void CopyTermThreeSeesTheSameSimplification()
    {
        // The top level is not a special case: copy_term/3 runs the same
        // projection, so an embedder collecting residual goals gets the
        // simplified set too.
        var e = Coroutining();
        Assert.True(e.Query(
            "dif(A, B), dif(g(A, B), g(C, C)), copy_term(f(A, B, C), _, Gs), "
            + "Gs = [dif(_, _)].").Success);
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Single-sided-unification (SSU) rules — <c>Head =&gt; Body</c> and
/// <c>Head, Guard =&gt; Body</c> — a first-class clause form of the engine (like
/// DCG <c>--&gt;</c>): committed, pattern-matching clauses.</summary>
public sealed class SingleSidedUnificationTests
{
    [Fact]
    public void CommitsToTheFirstMatchingClause()
    {
        var e = new PrologEngine();
        // Clause 3 would match anything, but clause 1 commits for 0.
        e.ConsultString(
            "sign(0, R) => R = zero.\n"
            + "sign(N, R), N > 0 => R = pos.\n"
            + "sign(_, R) => R = neg.\n");
        Assert.True(e.Query("sign(0, zero).").Success);
        Assert.True(e.Query("sign(5, pos).").Success);
        Assert.True(e.Query("sign(-3, neg).").Success);
    }

    [Fact]
    public void GuardFailure_FallsThroughToNextClause()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "grade(S, a), S >= 90 => true.\n"
            + "grade(S, b), S >= 80 => true.\n"
            + "grade(_, f) => true.\n");
        Assert.True(e.Query("grade(95, a).").Success);
        Assert.True(e.Query("grade(85, b).").Success);   // 85>=90 fails, 85>=80 holds
        Assert.True(e.Query("grade(50, f).").Success);   // both guards fail
        // The guard gates selection: 85 is not an 'a'.
        Assert.False(e.Query("grade(85, a).").Success);
    }

    [Fact]
    public void IsDeterministic_NoSpuriousBacktracking()
    {
        var e = new PrologEngine();
        // Two clauses whose heads both unify with p(1, X); the committed choice
        // means p(1, X) yields exactly ONE solution (the first).
        e.ConsultString(
            "p(1, R) => R = first.\n"
            + "p(_, R) => R = second.\n");
        Assert.Single(e.QueryAll("p(1, X)."));
        Assert.True(e.Query("p(1, first).").Success);
        Assert.False(e.Query("p(1, second).").Success);   // committed away
    }

    [Fact]
    public void PatternMatchingHead_LikeAssocStyleClauses()
    {
        var e = new PrologEngine();
        // The library shape SSU is written for: structural head patterns, one
        // clause per constructor, deterministic.
        e.ConsultString(
            "depth(leaf, 0) => true.\n"
            + "depth(node(L, R), D) => depth(L, DL), depth(R, DR), max(DL, DR, M), D is M + 1.\n"
            + "max(A, B, A), A >= B => true.\n"
            + "max(_, B, B) => true.\n");
        Assert.True(e.Query("depth(leaf, 0).").Success);
        Assert.True(e.Query("depth(node(leaf, node(leaf, leaf)), 2).").Success);
    }
}

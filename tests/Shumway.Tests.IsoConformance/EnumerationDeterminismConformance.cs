using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// A builtin that enumerates must leave a choice point only when a FURTHER
/// solution exists. Leaving one on the last (or only) answer is phantom
/// nondeterminism: every caller that does not cut drags a dead choice point
/// around, and lgtunit's deterministic/1 — which the conformity battery uses —
/// reports the goal as nondet.
///
/// <para>The rule these all follow: narrow the candidate set to the SOLUTIONS
/// before enumerating. Where the bound arguments say which candidates can
/// match, filter on them; where only a trial unification can tell, look ahead
/// with one (rolled back) before deciding to push.</para>
/// </summary>
public class EnumerationDeterminismConformance
{
    /// <summary>True when <paramref name="goal"/> succeeds and leaves no
    /// choice point. setup_call_cleanup runs its cleanup as soon as the goal
    /// completes deterministically, which is the portable way to ask.</summary>
    private static bool IsDeterministic(string setup, string goal)
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            $"{setup} setup_call_cleanup(true, ({goal}), Done = yes), nonvar(Done).");
        return sol.Success;
    }

    [Fact]
    public void AtomConcatWithAliasedArgumentsIsDeterministic()
    {
        // Both halves are the SAME unbound variable: only the even split can
        // match, so the split point is pinned exactly as a bound argument
        // pins it. `A == aa` is the answer, with no choice point behind it.
        Assert.True(IsDeterministic("", "atom_concat(A, A, aaaa), A == aa"));
        // …and the mode still fails where it must.
        var engine = new PrologEngine();
        Assert.False(engine.Query("atom_concat(A, A, abc).").Success);
        Assert.False(engine.Query("atom_concat(A, A, abcd).").Success);
        // The ordinary split enumeration is untouched.
        Assert.Equal(4, engine.QueryAll("atom_concat(X, Y, abc).").Count());
    }

    [Fact]
    public void BoundArgumentsNarrowTheCandidateSet()
    {
        Assert.True(IsDeterministic("", "current_op(P, T, xor), P == 400"));
        Assert.True(IsDeterministic(
            "assertz(det_cp(1)),", "current_predicate(det_cp/1)"));
        Assert.True(IsDeterministic(
            "assertz(det_pp(1)),", "predicate_property(det_pp(_), (dynamic))"));
        Assert.True(IsDeterministic("", "stream_property(_, alias(user_output))"));
    }

    [Fact]
    public void ClauseFiltersHeadsThatCannotMatch()
    {
        // First-argument indexing's question, asked at the AST level.
        Assert.True(IsDeterministic(
            "assertz(det_cl(1)), assertz(det_cl(2)),", "clause(det_cl(1), _)"));
        // Filtering must not lose solutions.
        var engine = new PrologEngine();
        engine.ConsultString("p(1, a). p(2, b). p(X, c) :- q(X). q(9).");
        Assert.Equal(2, engine.QueryAll("clause(p(1, _), _).").Count());
        Assert.Equal(3, engine.QueryAll("clause(p(_, _), _).").Count());
        Assert.Single(engine.QueryAll("clause(p(2, b), _)."));
    }

    [Fact]
    public void RecordedLooksAheadBeforePushing()
    {
        Assert.True(IsDeterministic(
            "recordz(dk, v1, _), recordz(dk, v2, _),", "recorded(dk, v2, _)"));
        // The enumeration itself still yields every match.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "recordz(k2, a, _), recordz(k2, b, _), "
            + "findall(V, recorded(k2, V, _), L), L == [a, b].").Success);
    }
}

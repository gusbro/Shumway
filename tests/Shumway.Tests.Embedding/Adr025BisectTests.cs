using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Regression suite for the HELPER-NAME COLLISION latent bug (found while
/// validating ADR-025, present at least since phase-32): MetaTransform's
/// synthesized-helper counter restarted at zero per transform run, so a QUERY
/// stub's `$disj_1` (e.g. the findall collect-loop) collided — same module
/// mangling, same arity — with a CONSULTED clause's `$disj_1` (a user
/// if-then-else): the query-region definition shadowed the consulted helper and
/// the caller executed the WRONG body (instantiation_error from garbage
/// registers). Fixed by a process-global helper-name sequence.
/// Plus arith-in-branch shapes for the ADR-025 inline lowering.
/// </summary>
public class HelperNameCollisionRegressionTests
{
    private static PrologEngine E(string p, bool inline = false)
    {
        var e = new PrologEngine { EnableInlineIte = inline };
        e.ConsultString(p);
        return e;
    }

    private const string Classify =
        "classify(X, R) :- (X > 0 -> R = pos ; R = nonpos).\n";

    [Fact]
    public void FindallOverConsultedIte_DefaultEngine()
    {
        // THE original repro: the findall query's own collect-loop helper used to
        // collide with classify's consulted `$disj` helper.
        var e = E(Classify);
        Assert.True(e.Query("findall(R, classify(5, R), L), L == [pos].").Success);
        Assert.True(e.Query("findall(R, classify(0, R), L), L == [nonpos].").Success);
    }

    [Fact]
    public void DirectQueryThenFindall()
    {
        var e = E(Classify);
        Assert.True(e.Query("classify(5, R), R == pos.").Success);
        Assert.True(e.Query("findall(R, classify(5, R), L), L == [pos].").Success);
    }

    [Fact]
    public void TwoEngines_SameProgram_BothFindall()
    {
        var e1 = E(Classify);
        var e2 = E(Classify);
        Assert.True(e1.Query("findall(R, classify(5, R), L), L == [pos].").Success);
        Assert.True(e2.Query("findall(R, classify(5, R), L), L == [pos].").Success);
    }

    [Fact]
    public void QueryDisjunctionOverConsultedDisjunction()
    {
        // A `;` in the QUERY over a consulted predicate that also synthesized a
        // `;` helper — the other collision-prone shape.
        var e = E(Classify);
        Assert.True(e.Query("( classify(5, R) ; R = fallback ), R == pos.").Success);
        Assert.True(e.Query(
            "findall(R, ( classify(-1, R) ; R = extra ), L), L == [nonpos, extra].").Success);
    }

    [Fact]
    public void NegationAndCatchHelpers_NoCollision()
    {
        var e = E("""
            safe(X) :- \+ bad(X).
            bad(0).
            """);
        Assert.True(e.Query("\\+ bad(1), safe(1).").Success);
        Assert.True(e.Query("findall(X, (member(X, [0, 1, 2]), safe(X)), L), L == [1, 2].").Success);
        Assert.True(e.Query("catch(safe(1), _, fail).").Success);
    }

    // ---- ADR-025 inline-lowering arith shapes (bisected during bring-up) ----

    [Fact] public void Inline_ArithInThen()
        => Assert.True(E("t(X,R) :- (X > 0 -> Y is X * 2, R = Y ; R = neg).", inline: true)
            .Query("t(3, R), R == 6.").Success);

    [Fact] public void Inline_ArithInElse()
        => Assert.True(E("t(X,R) :- (X > 100 -> R = big ; Y is X + 1, R = Y).", inline: true)
            .Query("t(3, R), R == 4.").Success);

    [Fact] public void Inline_SameVarBoundInBothBranches()
        // Regression for the emitter's Y-initialization tracking across branches:
        // A is first-bound in BOTH branches; the else path must not read it as
        // "already initialized" just because the then path was emitted first.
        => Assert.True(E("t(X,G) :- (X > 0 -> A = high ; A = low), G = g(A).", inline: true)
            .Query("t(-2, G), G == g(low).").Success);
}

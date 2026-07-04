using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-025 stage (b) — the inline if-then-else / disjunction shape compiles to
/// Tier-1 IL: mid-body <c>try_me_else</c> becomes an arity-0 IL choice point
/// whose resume re-enters the delegate at the ELSE cursor
/// (<c>IlIteHelper.Resume</c> + the chunk-218 marker protocol), <c>jump</c>
/// becomes an unconditional branch, and the chain describer follows dispatch
/// operands so a multi-clause host with an inner ITE still describes. Every
/// test runs PROMOTED (threshold 1) and asserts promotion actually happened —
/// a Tier-0 fallback would hide an emit failure.
/// </summary>
public class Adr025StageBTests
{
    private static (PrologEngine Engine, int Fid) Promoted(
        string program, string name, int arity, string warmQuery)
    {
        var e = new PrologEngine { EnableInlineIte = true };
        e.IlPromotion.Threshold = 1;
        e.ConsultString(program);
        // Two passes: record + promote.
        Assert.True(e.Query(warmQuery).Success);
        Assert.True(e.Query(warmQuery).Success);
        int fid = FunctorTable.Intern(AtomTable.Intern(name).Id, arity);
        Assert.True(e.IlPromotion.IsPromoted(fid),
            $"{name}/{arity} must promote to IL (stage-b emit)");
        return (e, fid);
    }

    [Fact]
    public void Ite_Promoted_ThenAndElsePaths()
    {
        var (e, _) = Promoted(
            ":- public classify/2.\n" +
            "classify(X, R) :- (X > 0 -> R = pos ; R = nonpos).\n",
            "classify", 2, "classify(5, R), R == pos.");
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query("classify(7, R), R == pos.").Success);
            Assert.True(e.Query("classify(-1, R), R == nonpos.").Success);
            Assert.True(e.Query("classify(0, R), R == nonpos.").Success);
            // Determinism: the else branch is unreachable once cond succeeded.
            Assert.True(e.Query("findall(R, classify(5, R), L), L == [pos].").Success);
        }
    }

    [Fact]
    public void Disjunction_Promoted_BacktracksIntoElse()
    {
        // THE stage-b-critical path: backtracking pops the inline CP and
        // re-enters the promoted delegate at the ELSE cursor.
        var (e, _) = Promoted(
            ":- public pick/1.\n" +
            "pick(X) :- (X = a ; X = b).\n",
            "pick", 1, "pick(a).");
        for (int i = 0; i < 3; i++)
        {
            var xs = e.QueryAll("pick(X).")
                .Select(s => s["X"]!.ToString()).ToList();
            Assert.Equal(new[] { "a", "b" }, xs);
        }
    }

    [Fact]
    public void Disjunction_Promoted_ContinuationAfterJoin_RunsOnBothPaths()
    {
        var (e, _) = Promoted(
            ":- public tag/2.\n" +
            "tag(X, Y) :- (X = a ; X = b), Y = t(X).\n",
            "tag", 2, "tag(a, _).");
        var ys = e.QueryAll("tag(X, Y).")
            .Select(s => s["Y"]!.ToString()).ToList();
        Assert.Equal(new[] { "t(a)", "t(b)" }, ys);
    }

    [Fact]
    public void Ite_Promoted_CommitsFirstCondSolution()
    {
        var (e, _) = Promoted(
            ":- public first/1.\n" +
            "q(a).\nq(b).\n" +
            "first(R) :- (q(X) -> R = X ; R = none).\n",
            "first", 1, "first(a).");
        // The inline cut must prune the ITE CP AND the condition's CPs.
        Assert.True(e.Query("findall(R, first(R), L), L == [a].").Success);
    }

    [Fact]
    public void TwoItes_InOneBody_Promoted()
    {
        var (e, _) = Promoted(
            ":- public both/3.\n" +
            "both(X, Y, R) :- (X > 0 -> A = xp ; A = xn), (Y > 0 -> B = yp ; B = yn), R = A-B.\n",
            "both", 3, "both(1, 1, R), R == xp-yp.");
        Assert.True(e.Query("both(1, -1, R), R == xp-yn.").Success);
        Assert.True(e.Query("both(-1, 1, R), R == xn-yp.").Success);
        Assert.True(e.Query("both(-1, -1, R), R == xn-yn.").Success);
    }

    [Fact]
    public void MultiClauseHost_WithInnerIte_DescribesAndPromotes()
    {
        // Clause 2 carries the ITE: the try_me_else chain describer must
        // follow the dispatch operands (a linear me-else scan would count the
        // inner try_me_else/trust_me as clause boundaries and reject).
        var (e, _) = Promoted(
            ":- public grade/2.\n" +
            "grade(stop, halt) :- !.\n" +
            "grade(X, R) :- (X >= 60 -> R = pass ; R = fail).\n",
            "grade", 2, "grade(70, R), R == pass.");
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query("grade(stop, R), R == halt.").Success);
            Assert.True(e.Query("grade(90, R), R == pass.").Success);
            Assert.True(e.Query("grade(30, R), R == fail.").Success);
        }
    }

    [Fact]
    public void Ite_Promoted_InsideFindallAndCallers()
    {
        // The promoted ITE predicate reached through meta-call machinery.
        var (e, _) = Promoted(
            ":- public sign/2.\n" +
            "sign(X, R) :- (X > 0 -> R = 1 ; R = 0).\n",
            "sign", 2, "sign(4, R), R == 1.");
        Assert.True(e.Query(
            "findall(R, (member(X, [3, -2, 7, 0]), sign(X, R)), L), L == [1, 0, 1, 0].").Success);
        Assert.True(e.Query("G = sign(-5, R), call(G), R == 0.").Success);
    }

    // ---- Bring-up regressions found by the stage-(d) measurement (boyer) ----

    [Fact]
    public void PreIteGenerator_ChoicePointsSurviveTheIteCut()
    {
        // get_level captured B0 — which the pre-ITE call to g/1 had reset to
        // the value BEFORE g's choice point — so the ITE's commit cut pruned
        // the generator's CP (lost solutions / crashed). get_level_b captures
        // CURRENT B at the try point: the cut pops exactly the ITE CP + the
        // condition's CPs.
        const string program =
            ":- public g/1.\ng(1).\ng(2).\ng(3).\n" +
            ":- public p/2.\n" +
            "p(X, R) :- g(X), (X > 1 -> R = big ; R = small).\n";
        foreach (int threshold in new[] { 0, 1 })
        {
            var e = new PrologEngine { EnableInlineIte = true };
            e.IlPromotion.Threshold = threshold;
            e.ConsultString(program);
            for (int i = 0; i < 2; i++)
            {
                var xs = e.QueryAll("p(X, R).")
                    .Select(s => $"{s["X"]}-{s["R"]}").ToList();
                Assert.Equal(new[] { "1-small", "2-big", "3-big" }, xs);
            }
        }
    }

    [Fact]
    public void PreIteCall_EnvTrim_KeepsTheBarrierSlotAlive()
    {
        // The ITE barrier Y slot sits ABOVE the named permanents; a call
        // BEFORE the ITE used to trim the frame below it, letting the
        // condition's callee overwrite the slot — a garbage cut barrier
        // (boyer's Cut→CompactTrails IndexOutOfRange). The liveness analysis
        // now folds the barrier slot in, like the deep-cut slot.
        const string program =
            ":- public ax/2.\nax(f(X), X).\n" +
            ":- public step/2.\n" +
            ":- public rw/2.\n" +
            "step([], []).\n" +
            "step([H|T], [H|T2]) :- step(T, T2).\n" +
            "rw(T, R) :- step([1,2,3], _L), (ax(T, N) -> rw(N, R) ; R = T).\n";
        foreach (int threshold in new[] { 0, 1 })
        {
            var e = new PrologEngine { EnableInlineIte = true };
            e.IlPromotion.Threshold = threshold;
            e.ConsultString(program);
            for (int i = 0; i < 2; i++)
            {
                Assert.True(e.Query("rw(f(f(f(done))), R), R == done.").Success);
                Assert.True(e.Query("rw(plain, R), R == plain.").Success);
            }
        }
    }

    [Fact]
    public void Boyer_InlineMatchesHelper_BothTiers()
    {
        // The real program that surfaced both bring-up bugs.
        string src = System.IO.File.ReadAllText(
            System.IO.Path.Combine(FindRepoRoot(), "benchmarks", "vanroy", "boyer.pl"));
        foreach (bool inline in new[] { false, true })
            foreach (int threshold in new[] { 0, 1 })
            {
                var e = new PrologEngine { EnableInlineIte = inline };
                e.IlPromotion.Threshold = threshold;
                e.ConsultString(src);
                Assert.True(e.Query("bench(10).").Success,
                    $"boyer bench(10) inline={inline} t={threshold}");
            }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    [Fact]
    public void Differential_InlineIlVsHelperTier0()
    {
        // Same program, four configurations (inline×IL, inline×Tier-0,
        // helper×IL, helper×Tier-0) must agree on the full solution set.
        const string program =
            "e(X, R) :- (X mod 2 =:= 0 -> R = even ; R = odd).\n" +
            "all(L) :- findall(X-R, (between(1, 6, X), e(X, R)), L).\n";
        string? expected = null;
        foreach (bool inline in new[] { true, false })
            foreach (int threshold in new[] { 0, 1 })
            {
                var e = new PrologEngine { EnableInlineIte = inline };
                e.IlPromotion.Threshold = threshold;
                e.ConsultString(program);
                for (int i = 0; i < 2; i++)
                {
                    var sol = e.QueryAll("all(L).").Single()["L"]!.ToString();
                    if (expected is null) expected = sol;
                    else Assert.Equal(expected, sol);
                }
            }
    }

    // ---- ADR-025 (ITE in regions) — a local member containing an inline ITE
    //      now stays IN the region: the planner gives its try_me_else pc an
    //      ELSE re-entry cursor, the CP carries the REGION delegate + cursor,
    //      TrustMe marks the label. These run with regions at their default
    //      (ON) and a PUBLIC root over LOCAL members so a region forms. ----

    [Fact]
    public void RegionMember_CallCondIte_AllPaths()
    {
        // step/2's cond is a CALL (axiom-style, can bind + leave bucket CPs);
        // then-branch tail-recurses (branch-tail LCO inside the region).
        var (e, _) = Promoted(
            ":- public go/2.\n" +
            "go(X, R) :- prep(X, Y), step(Y, R).\n" +
            "step(Y, R) :- ( tbl(Y, Z) -> step(Z, R) ; R = Y ).\n" +
            "prep(X, Y) :- Y is X + 1.\n" +
            "tbl(1, 10).\ntbl(10, 20).\ntbl(2, 30).\n",
            "go", 2, "go(0, R), R == 20.");
        Assert.True(e.Query("go(0, R), R == 20.").Success);   // then-chain
        Assert.True(e.Query("go(3, R), R == 4.").Success);    // else at once
        Assert.True(e.Query("go(1, R), R == 30.").Success);   // else mid-chain
        Assert.True(e.Query("findall(R, go(0, R), L), L == [20].").Success);
    }

    [Fact]
    public void RegionMember_Ite_BacktracksAcrossCommit()
    {
        // A generator BEFORE the ITE member: each solution re-enters pick/2,
        // whose ITE commits per-solution without pruning gen/1's CPs.
        var (e, _) = Promoted(
            ":- public multi/1.\n" +
            "multi(R) :- gen(X), pick(X, R).\n" +
            "pick(X, R) :- ( X > 1 -> R = big(X) ; R = small(X) ).\n" +
            "gen(1).\ngen(2).\ngen(3).\n",
            "multi", 1, "multi(_).");
        var all = e.QueryAll("multi(R).")
            .Select(s => s["R"]!.ToString()).ToList();
        Assert.Equal(new[] { "small(1)", "big(2)", "big(3)" }, all);
    }

    [Fact]
    public void RegionMember_Ite_DeepTailRecursion_ConstantStack()
    {
        // Branch-tail LCO through the then-branch inside a region: 200k
        // iterations must run in constant control stack (the pre-LCO shape
        // was O(depth) frames — a stack-robustness regression).
        var (e, _) = Promoted(
            ":- public down/2.\n" +
            "down(N, R) :- dstep(N, R).\n" +
            "dstep(N, R) :- ( N > 0 -> N1 is N - 1, dstep(N1, R) ; R = done ).\n",
            "down", 2, "down(5, R), R == done.");
        Assert.True(e.Query("down(200000, R), R == done.").Success);
    }
}

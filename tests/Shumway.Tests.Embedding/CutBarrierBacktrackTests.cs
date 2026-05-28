using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Regression for the deep-cut barrier bug surfaced running Blint: a
/// clause's nested <c>Call</c> overwrote the global cut-barrier register
/// (<c>_b0</c>), and backtracking into a later clause did not restore it,
/// so that clause's <c>!</c> read a stale barrier and committed to the
/// wrong choice point — the predicate's own clause-selection CP survived
/// the cut and a subsequent clause ran anyway.
///
/// The fix saves <c>_b0</c> in every choice point and restores it on
/// retry/trust, so a deep cut always sees the enclosing predicate's
/// entry barrier.
/// </summary>
public class CutBarrierBacktrackTests
{
    // The shape that broke: clause 1's body calls a user predicate that
    // fails (leaving the clause-selection CP active), clause 2 succeeds
    // with a deep cut, and an outer failure-driven loop backtracks. The
    // cut must remove clause 3's choice point.
    private const string Program =
        ":- public run/1.\n"
        + "g(_) :- fail.\n"
        + "chk(P) :- g(P), !, fail.\n"        // clause 1 — body fails before its cut
        + "chk(_) :- !.\n"                     // clause 2 — deep cut, commits
        + "chk(_) :- record_fellthrough.\n"   // clause 3 — must NOT run
        + ":- dynamic fellthrough/0.\n"
        + "record_fellthrough :- assertz(fellthrough).\n"
        + "run(R) :- ( chk(x), fail ; true ),\n"
        + "          ( fellthrough -> R = leaked ; R = ok ).\n";

    [Fact]
    public void DeepCut_AfterFailedNestedCall_CommitsAcrossBacktrack()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Program);
        var sol = engine.Query("run(R).");
        Assert.True(sol.Success);
        Assert.Equal("ok", sol.Bindings["R"].ToString());
    }

    [Fact]
    public void DeepCut_FromBundle_CommitsAcrossBacktrack()
    {
        // Same shape, but loaded from a linked bundle — the path Blint
        // actually exercised.
        var obj = ShmoCompiler.CompileSource(Program, "cuttest",
            ShmoBuildMode.Release);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("run", 1) },
        });
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Select(d => d.Message)));

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        var sol = engine.Query("run(R).");
        Assert.True(sol.Success);
        Assert.Equal("ok", sol.Bindings["R"].ToString());
    }
}

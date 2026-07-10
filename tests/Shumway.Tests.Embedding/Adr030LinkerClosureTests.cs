using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-030 linker closure — the whole-program determinism fixpoint at link
/// time. The elidable shape is a LAST clause ending in a trailing top-level
/// <c>!</c> whose prefix calls a CROSS-MODULE callee: intra-module the callee
/// is opaque (CrossModule blocker), but the linker owns every module's clauses,
/// so the fixpoint resolves it and elides the cut when the callee is det.
/// Purely semantics-preserving — every test checks the observable behaviour is
/// identical to the cut-bearing original.
/// </summary>
public class Adr030LinkerClosureTests
{
    private static LinkResult Link(params (string Module, string Source)[] mods) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = mods.Select(m => ShmoCompiler.CompileSource(m.Source, m.Module)).ToArray(),
            EntryPoints = new[] { new PredicateRef("main", 2) },
        });

    private static PrologEngine Load(LinkResult r)
    {
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(r.Bytes!));
        return e;
    }

    private static bool Elided(LinkResult r) =>
        r.Diagnostics.Any(d => d.Message.Contains("redundant trailing cut"));

    [Fact]
    public void CrossModuleDetCallee_TrailingCutElided_SemanticsIntact()
    {
        // a:main/2's LAST clause ends `check(X), R = pos, !.` — check/1 is
        // module b's PUBLIC single-clause det predicate. Intra-module the cut
        // was CrossModule-blocked; the linker closure proves check/1 det and
        // drops the trailing cut.
        var r = Link(
            ("a", ":- public main/2.\n"
                + "main(X, R) :- X < 0, !, R = neg.\n"
                + "main(X, R) :- check(X), R = pos, !.\n"),
            ("b", ":- public check/1.\n"
                + "check(X) :- X > 0.\n"));
        Assert.True(Elided(r));
        var e = Load(r);
        Assert.True(e.Query("main(5, R), R == pos.").Success);
        Assert.Single(e.QueryAll("main(5, R)."));
        Assert.True(e.Query("main(-5, R), R == neg.").Success);
        Assert.Single(e.QueryAll("main(-5, R)."));
        Assert.False(e.Query("main(0, R).").Success);   // both clauses fail
    }

    [Fact]
    public void CrossModuleNondetCallee_TrailingCutKept()
    {
        // b:pick/1 is NONDET — the fixpoint must NOT prove it det: the last
        // clause's trailing cut commits to pick's first solution and must stay
        // (eliding it would leak a second answer for a free X).
        var r = Link(
            ("a", ":- public main/2.\n"
                + "main(X, R) :- X == z, !, R = zed.\n"
                + "main(X, R) :- pick(X), R = got, !.\n"),
            ("b", ":- public pick/1.\n"
                + "pick(one).\n"
                + "pick(two).\n"));
        Assert.False(Elided(r));
        var e = Load(r);
        Assert.Single(e.QueryAll("main(X, R)."));       // committed to pick(one)
        Assert.True(e.Query("main(X, R), X == one, R == got.").Success);
    }

    [Fact]
    public void CrossModuleChain_FixpointResolvesTransitively()
    {
        // a → b:outer → b:inner (both det) — determinism chains across the
        // module boundary through the whole-program fixpoint.
        var r = Link(
            ("a", ":- public main/2.\n"
                + "main(X, R) :- X < 0, !, R = neg.\n"
                + "main(X, R) :- outer(X), R = big, !.\n"),
            ("b", ":- public outer/1.\n"
                + "outer(X) :- inner(X).\n"
                + "inner(X) :- X > 10.\n"));
        Assert.True(Elided(r));
        var e = Load(r);
        Assert.True(e.Query("main(50, R), R == big.").Success);
        Assert.Single(e.QueryAll("main(50, R)."));
        Assert.True(e.Query("main(-1, R), R == neg.").Success);
        Assert.False(e.Query("main(3, R).").Success);   // 3 not < 0, not > 10
    }

    [Fact]
    public void DynamicCallee_NeverDet_TrailingCutKept()
    {
        // b:flag/1 is dynamic — resolvable by name but its clause set changes at
        // runtime, so it never enters the det set and the cut stays.
        var r = Link(
            ("a", ":- public main/2.\n"
                + "main(X, R) :- X == z, !, R = zed.\n"
                + "main(X, R) :- flag(X), R = up, !.\n"),
            ("b", ":- public flag/1.\n"
                + ":- dynamic flag/1.\n"
                + "flag(on).\n"
                + "flag(off).\n"));
        Assert.False(Elided(r));
        var e = Load(r);
        Assert.Single(e.QueryAll("main(X, R)."));       // committed to flag(on)
        Assert.True(e.Query("main(X, R), X == on.").Success);
    }

    [Fact]
    public void IntraModuleGuard_ThroughTheLinker_Behaves()
    {
        // Single-module sanity through the link + recompile path: the local det
        // callee's trailing cut elides (the intra rule, now applied at link).
        var r = Link(
            ("a", ":- public main/2.\n"
                + "local(X) :- X > 0.\n"
                + "main(X, R) :- X < 0, !, R = neg.\n"
                + "main(X, R) :- local(X), R = pos, !.\n"));
        var e = Load(r);
        Assert.True(e.Query("main(3, R), R == pos.").Success);
        Assert.Single(e.QueryAll("main(3, R)."));
        Assert.True(e.Query("main(-3, R), R == neg.").Success);
        Assert.Single(e.QueryAll("main(-3, R)."));
    }
}

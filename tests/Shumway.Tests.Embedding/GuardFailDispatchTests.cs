using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-031 head facts in an indexed predicate: every guard that fails goes on
/// through one dispatch on the next node's cursor. A switch over the cursors
/// in each clause made the method's flow graph quadratic in the clauses, and
/// the JIT took seconds on a table of a few hundred facts. The measure is the
/// switch targets of the emitted method: four times the clauses, about four
/// times the targets; a switch per clause gives sixteen.
/// </summary>
// Switches compiler statics: it runs alone, in the gate's exclusive pass.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public class GuardFailDispatchTests
{
    private static string Table(string name, int n)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i < n; i++) sb.Append($"{name}({i}, {i + 1}).\n");
        return sb.ToString();
    }

    private static int Fid(string name, int arity) => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    // The method of a predicate of its own, not a region.
    private static IlPredicateCompiler.EmitShape Emitted(string source, string root, int arity)
    {
        var module = new ModuleCompiler().Compile(new ClauseReader(source).ReadAll().ToList());
        var map = module.Predicates.ToDictionary(p => p.FunctorId);
        bool rc = IlPredicateCompiler.RegionCompile, one = IlPredicateCompiler.OneMemberRegions;
        IlPredicateCompiler.RegionCompile = false;
        IlPredicateCompiler.OneMemberRegions = false;
        try
        {
            IlPredicateCompiler.LastEmitShape = null;
            Assert.NotNull(new IlPredicateCompiler().Compile(map[Fid(root, arity)], map));
            return IlPredicateCompiler.LastEmitShape ?? throw new Xunit.Sdk.XunitException("no method emitted");
        }
        finally
        {
            IlPredicateCompiler.RegionCompile = rc;
            IlPredicateCompiler.OneMemberRegions = one;
        }
    }

    [Fact]
    public void AFactTableIsAMethodLinearInItsClauses()
    {
        long heads = IlPredicateCompiler.CpFreeGuardStats.HeadFact;
        var small = Emitted(Table("gfd_t", 100), "gfd_t", 2);
        var large = Emitted(Table("gfd_t", 400), "gfd_t", 2);
        // ANTI-VACUITY: the clauses were compiled as head facts.
        Assert.True(IlPredicateCompiler.CpFreeGuardStats.HeadFact - heads >= 400,
            $"head facts: {IlPredicateCompiler.CpFreeGuardStats.HeadFact - heads}");
        Assert.True(large.SwitchTargets < 6 * small.SwitchTargets,
            $"switch targets: {small.SwitchTargets} for 100 clauses, {large.SwitchTargets} for 400");
    }

    private static string Walk(int n) =>
        ":- public gfr_walk/2.\ngfr_walk(X, Y) :- gfr_a(X, Z), gfr_b(Z, Y).\n" + Table("gfr_a", n) + Table("gfr_b", n);

    private static int RegionMembers(int rootFid)
        => IlRegionCost.Emissions.Where(r => r.RootFid == rootFid).Select(r => r.Members).DefaultIfEmpty(0).Max();

    // The region gfr_walk/2 promotes to, with both tables in it.
    private static IlRegionCost.Emission RegionOf(int n)
    {
        IlRegionCost.ResetDiagnostics();
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Walk(n));
        Assert.Equal(n - 2, e.QueryAll("gfr_walk(X, Y).").Count());
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        // ANTI-VACUITY: both tables are members of the region.
        int root = Fid("gfr_walk", 2);
        Assert.True(RegionMembers(root) == 3, $"{n} facts a table: {RegionMembers(root)} members");
        return IlRegionCost.Emissions.Last(r => r.RootFid == root && r.Members == 3);
    }

    // Tables small enough for the region's budget: three times the clauses,
    // about three times the targets; a switch per clause gives nine.
    [Fact]
    public void ARegionOfFactTablesIsAMethodLinearInTheirClauses()
    {
        var small = RegionOf(4);
        var large = RegionOf(10);
        Assert.True(large.SwitchTargets < 5 * small.SwitchTargets,
            $"switch targets: {small.SwitchTargets} for 2x3 clauses, {large.SwitchTargets} for 2x9");
    }

    // The guards of two members fail into the one dispatch: each goes on at
    // its own member's next node, every solution in order, as on Tier-0.
    [Fact]
    public void TheGuardsOfTwoMembersFailIntoTheirOwnNextNodes()
    {
        const string program = """
            :- public gfs_walk/2.
            gfs_walk(X, Y) :- gfs_a(X, Z), gfs_b(Z, Y).
            gfs_a(1, p). gfs_a(2, q). gfs_a(3, p). gfs_a(4, r). gfs_a(5, q).
            gfs_b(q, x). gfs_b(p, y). gfs_b(r, z). gfs_b(p, w). gfs_b(q, v).
            """;
        string[] goals = { "gfs_walk(X, Y)", "X = 3, gfs_walk(X, Y)", "Y = w, gfs_walk(X, Y)", "gfs_walk(X, X), Y = X" };
        var oracle = new PrologEngine();
        oracle.IlPromotion.Threshold = 0;
        oracle.ConsultString(program);

        IlRegionCost.ResetDiagnostics();
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(program);
        foreach (string g in goals) e.QueryAll(g + ".").ToList();
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        // ANTI-VACUITY: the root runs a region with both tables in it.
        Assert.Equal(3, RegionMembers(Fid("gfs_walk", 2)));
        foreach (string g in goals)
            Assert.Equal(
                oracle.QueryAll(g + ".").Select(s => $"{s["X"]}-{s["Y"]}").ToArray(),
                e.QueryAll(g + ".").Select(s => $"{s["X"]}-{s["Y"]}").ToArray());
    }
}

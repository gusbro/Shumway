using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A module-qualified goal, <c>M:G</c>, that the compiler could not
/// resolve statically and so left as a call to <c>':'/2</c>.
///
/// <para><c>':'/2</c> is not what its clauses say it is. It carries a module
/// and a goal, and the INTERPRETER intercepts it inside its own dispatch;
/// the clauses exist to have something to name. Compiled to the tier it ran
/// as written, and <c>lists:append([1],[2],X)</c> came back as an
/// existence_error naming the MODULE ATOM -- not even the goal -- while
/// Tier 0 answered.</para>
///
/// <para>Found through a probe that compared clp(Z) domains across tiers and
/// used a qualified call to reach <c>fd_dom/2</c>.</para></summary>
public sealed class QualifiedGoalOnTierTests(ITestOutputHelper o)
{
    private static string Tiered(string program, string goal)
    {
        try
        {
            var (t, _) = TieredEngine.Build(program);
            return t.Query(goal).Success ? "ok" : "failed";
        }
        catch (System.Exception ex) { return ex.Message; }
    }

    private static string Plain(string program, string goal)
    {
        try
        {
            var e = new PrologEngine();
            e.ConsultString(program);
            return e.Query(goal).Success ? "ok" : "failed";
        }
        catch (System.Exception ex) { return ex.Message; }
    }

    /// <summary>Every shape a qualified goal can take answers the same on
    /// both tiers. Only the first one was broken, and the other three are
    /// what said so: they rule out the qualification itself, the module
    /// boundary, and the meta-call path, leaving the runtime <c>':'/2</c>.
    /// </summary>
    [Theory]
    [InlineData("a qualified call the compiler leaves to run time",
        ":- use_module(library(lists)).\nq(X) :- lists:append([1],[2],X).",
        "q(X), X == [1,2].")]
    [InlineData("the same predicate, imported and unqualified",
        ":- use_module(library(lists)).\nq(X) :- append([1],[2],X).",
        "q(X), X == [1,2].")]
    [InlineData("qualified, reached through a variable goal",
        ":- use_module(library(lists)).\nq(X) :- G = lists:append([1],[2],X), call(G).",
        "q(X), X == [1,2].")]
    [InlineData("qualified to a module the compiler CAN resolve",
        "helper(A, B, C) :- C = h(A, B).\nq(X) :- user:helper(1, 2, X).",
        "q(X), X == h(1,2).")]
    public void AQualifiedGoalAnswersTheSameOnBothTiers(
        string label, string program, string goal)
    {
        string plain = Plain(program, goal);
        string tier = Tiered(program, goal);
        o.WriteLine($"{label}: tier0={plain} tier={tier}");
        Assert.Equal(plain, tier);
    }

    /// <summary>And the mechanism, stated directly: the tier does not take
    /// <c>':'/2</c>. Asserting the answer alone would go green again the day
    /// something else happens to paper over it.</summary>
    [Fact]
    public void TheTierDoesNotTakeTheQualificationPredicate()
    {
        var (t, members) = TieredEngine.Build(
            ":- use_module(library(lists)).\nq(X) :- lists:append([1],[2],X).");
        Assert.True(t.Query("q(X), X == [1,2].").Success);

        var taken = new List<string>();
        foreach (var m in members)
        {
            var (aid, ar) = FunctorTable.Lookup(m.Predicate.FunctorId);
            taken.Add($"{AtomTable.GetById(aid)?.Name}/{ar}");
        }
        o.WriteLine("promoted: " + string.Join(", ", taken));
        // ANTI-VACUITY: the corpus's own predicate IS taken, so a run that
        // promoted nothing at all cannot pass this.
        Assert.Contains("user$q/1", taken);
        Assert.DoesNotContain(":/2", taken);
    }
}

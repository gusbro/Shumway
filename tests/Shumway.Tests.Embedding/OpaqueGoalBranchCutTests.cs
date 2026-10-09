using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A <c>!</c> in a <c>;</c> arm or a <c>-&gt;</c> then of a goal that
/// is a cut barrier of its own (once/1, ignore/1, \+, catch/3, the goal of
/// findall/bagof/setof) commits that whole goal, as it would the goal of
/// call/1: not only the disjunction it is written in. Each answer is
/// SICStus'; ignore/1 is <c>(call(G) -> true ; true)</c>.</summary>
public sealed class OpaqueGoalBranchCutTests
{
    private static readonly Dictionary<string, string> Goals = new()
    {
        ["once"] = @"\+ once((member(X, [1,2,3]), (X >= 2 -> ! ; true), X > 2))",
        ["not"] = @"\+ (member(X, [1,2,3]), (X >= 2 -> ! ; true), X > 2)",
        ["ignore"] = "ignore((member(X, [1,2,3]), (X >= 2 -> ! ; true), X > 2)), var(X)",
        ["ignore_top"] = "ignore((!, fail))",
        ["catch"] = @"\+ catch((member(X, [1,2,3]), (X >= 2 -> ! ; true), X > 2), _, true)",
        ["findall"] = "findall(X, (member(X, [1,2,3]), (X >= 2 -> ! ; true)), L), L == [1,2]",
        ["findall_arm"] = "findall(X, (member(X, [1,2,3]), (X >= 2, ! ; true)), L), L == [1,2]",
        ["findall_catch"] =
            "findall(X, catch((member(X, [1,2,3]), (X >= 2 -> ! ; true)), _, true), L), L == [1,2]",
        ["bagof"] = "bagof(X, (member(X, [3,1,2]), (X =< 1 -> ! ; true)), L), L == [3,1]",
        ["setof"] = "setof(X, (member(X, [3,1,2]), (X =< 1 -> ! ; true)), L), L == [1,3]",
        ["once_inside"] = "findall(X-Y, (member(X, [1,2]), "
            + "once((member(Y, [a,b,c]), (Y == b -> ! ; fail)))), L), L == [1-b, 2-b]",
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string name in Goals.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void InAClause(string which)
    {
        var e = new PrologEngine();
        e.ConsultString(string.Join("\n", Goals.Select(kv => $"c({kv.Key}) :- {kv.Value}.")));
        Assert.True(e.Query($"c({which}).").Success, which);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AtTheTopLevel(string which)
        => Assert.True(new PrologEngine().Query(Goals[which] + ".").Success, which);
}

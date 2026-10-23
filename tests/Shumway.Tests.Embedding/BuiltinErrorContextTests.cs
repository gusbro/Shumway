using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The context of an error names the builtin that raised it, as
/// Name/Arity, the same in the interpreter and in Tier-1 code: an error from
/// the builtins library, one an embedding builtin builds with the engine in
/// hand, throw/1 of an unbound ball, an evaluation, a meta-call, and one
/// the engine's machinery raises inside a builtin.</summary>
public sealed class BuiltinErrorContextTests
{
    private static readonly (string Goal, string Context)[] Cases =
    {
        ("p1", "/(atom_length, 2)"),
        ("p2", "/(op, 3)"),
        ("p3", "/(throw, 1)"),
        ("p4", "/(is, 2)"),
        ("p5", "/(call, 1)"),
        ("p6", "/(set_output, 1)"),
    };

    private const string Program = @"
:- public p1/0.
:- public p2/0.
:- public p3/0.
:- public p4/0.
:- public p5/0.
:- public p6/0.
p1 :- atom_codes(abc, _), atom_length(1, _).
p2 :- atom_codes(abc, _), op(1201, xfx, foo).
p3 :- atom_codes(abc, _), throw(_).
p4 :- atom_codes(abc, _), X = foo, _ is X + 1.
p5 :- atom_codes(abc, _), G = _, call(G).
p6 :- atom_codes(abc, _), set_output(user_input).
";

    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static string Context(PrologEngine e, string goal)
    {
        var sol = e.Query($"catch({goal}, error(_, C), true).");
        Assert.True(sol.Success, goal);
        return sol["C"]?.ToString() ?? "";
    }

    private static string Contexts(PrologEngine e)
    {
        var all = new System.Collections.Generic.List<string>();
        foreach (var (goal, _) in Cases) all.Add(goal + " " + Context(e, goal));
        return string.Join("; ", all);
    }

    [Fact]
    public void TheContextNamesTheBuiltinOnBothTiers()
    {
        var tier0 = new PrologEngine();
        tier0.IlPromotion.Threshold = 0;
        tier0.ConsultString(Program);
        string expected = Contexts(tier0);
        foreach (var (goal, context) in Cases)
            if (context is not null) Assert.Contains(goal + " " + context, expected);

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.IlPromotion.BackgroundCompilation = false;
        tier1.ConsultString(Program);
        for (int i = 0; i < 3; i++) Contexts(tier1);
        Assert.True(tier1.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        foreach (var (goal, _) in Cases)
            Assert.True(tier1.IlPromotion.IsPromoted(Fid(goal, 0)), goal + "/0 must be Tier-1 IL");
        Assert.Equal(expected, Contexts(tier1));
    }
}

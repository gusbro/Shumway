using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A control construct handed to <c>call/N</c> as an atom with its
/// two goals appended (<c>call(;, P, Q)</c>) runs P and Q in the module of the
/// clause that called it, as <c>call((P ; Q))</c> does: the dispatcher tags
/// the appended goals with the module (ADR-056), an if-then-else among them
/// distributed into rather than wrapped. Both dispatchers, so both tiers.</summary>
public sealed class ControlConstructAppendedGoalsTests
{
    private const string Program = """
        :- module(cca_m, [pick/2, ite/2]).
        p(1).
        p(2).
        q(3).
        pick(Op, L) :- findall(X, call(Op, p(X), q(X)), L).
        ite(C, R) :- call(;, (C -> R = yes), R = no).
        """;

    // user's own p/1 and q/1: the module's goals must not reach them.
    private const string UserProgram = """
        p(99).
        q(99).
        """;

    private static string Name(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    private static PrologEngine Engine(bool compiled)
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        e.IlPromotion.Threshold = compiled ? 1 : 0;
        e.ConsultString(Program);
        e.ConsultString(UserProgram);
        if (!compiled) return e;
        var promoted = new HashSet<string>();
        for (int round = 0; round < 5 && !(promoted.Contains("pick/2") && promoted.Contains("ite/2")); round++)
        {
            Assert.True(e.Query("pick(;, _), ite(p(1), _).").Success);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            promoted = e.IlPromotion.PromotedFunctorIds().Select(Name).ToHashSet();
        }
        Assert.True(promoted.Contains("pick/2") && promoted.Contains("ite/2"),
            $"the module's predicates did not reach the IL tier\n{e.IlPromotion.DescribePromotionState()}");
        return e;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADisjunctionsGoals_RunInTheModule(bool compiled)
    {
        var e = Engine(compiled);
        Assert.True(e.Query("pick(;, L), L == [1, 2, 3].").Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AConjunctionsGoals_RunInTheModule(bool compiled)
    {
        var e = Engine(compiled);
        // p(X), q(X) share no X in the module; user's p(99), q(99) would.
        Assert.True(e.Query("pick(',', L), L == [].").Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIfThenElseAmongTheGoals_IsStillOne(bool compiled)
    {
        var e = Engine(compiled);
        Assert.True(e.Query("ite(p(1), R1), R1 == yes, ite(p(9), R2), R2 == no.").Success);
    }
}

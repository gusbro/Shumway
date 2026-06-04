using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>Argument-register preferencing: a head-extracted temporary whose
/// single body use is a first-goal call argument is unified straight into that
/// call's argument register, dropping the redundant put_value_x move.</summary>
public class ArgRegisterPreferencingTests
{
    private static string Conc =>
        PredicateDisassembler.Disassemble(
            "conc([], L, L).\nconc([H|T], L, [H|R]) :- conc(T, L, R).",
            new[] { ("conc", 3) }).Single().Text;

    [Fact]
    public void InPlaceListWalk_ExtractsIntoArgRegister_NoPutValue()
    {
        // conc([H|T],L,[H|R]) :- conc(T,L,R): T flows back to arg 0 and R to
        // arg 2 — both extracted straight into their call registers, so the
        // recursive clause emits no put_value_x at all.
        string text = Conc;
        Assert.DoesNotContain("put_value_x", text);
        Assert.Contains("unify_variable_x  [0]", text);   // T into reg 0
        Assert.Contains("unify_variable_x  [2]", text);   // R into reg 2
    }

    [Fact]
    public void HeadArgIsVariable_NotPreferenced_KeepsMove()
    {
        // p(X, [Y|T]) :- q(T, X): T would flow to arg 0, but arg 0 holds the
        // head variable X (live in the body), so reg 0 is NOT free — T must
        // keep its own slot and the put_value_x stays.
        string text = PredicateDisassembler
            .Disassemble("p(X, [Y|T]) :- q(T, X).").Single().Text;
        Assert.Contains("put_value_x", text);
    }

    [Fact]
    public void UsedInLaterGoal_NotPreferenced()
    {
        // r([X|T]) :- a(b), s(T): T is used only in the SECOND goal, so it is
        // permanent (Y) and never preferenced into a first-goal register.
        string text = PredicateDisassembler
            .Disassemble("r([X|T]) :- a(b), s(T).").Single().Text;
        Assert.Contains("unify_variable_y", text);   // T is a permanent
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-051 phase 0: a CLP(FD) domain is a term on the heap, not a
/// managed object named by a Foreign cell.
///
/// <para>These assert the REPRESENTATION, which is the part the rest of the
/// arc depends on: a wasm module can read a term out of linear memory and
/// cannot read a managed object, and a term is reclaimed by backtracking
/// where a foreign-table entry lives as long as the activation. The solver's
/// behaviour is covered by the clpfd and clpz suites, which are the oracle
/// for phase 0 and were green before this file existed.</para></summary>
public sealed class FdDomainOnTheHeapTests
{
    private static PrologEngine Fd()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).\n");
        return e;
    }

    private static void Holds(string goal)
        => Assert.True(Fd().Query(goal + ".").Success, goal);

    /// <summary>The shape itself. If a domain went back to being opaque,
    /// every one of these stops unifying.</summary>
    [Theory]
    [InlineData("'$dom_new'(1, 9, D), D = '$fd_dom'(1, 9)")]
    [InlineData("'$dom_new'(3, 3, D), D = '$fd_dom'(3, 3)")]
    [InlineData("'$dom_new'(1, 9, D), '$dom_del'(D, 5, D2), D2 = '$fd_dom'(1, 4, 6, 9)")]
    [InlineData("'$dom_new'(5, 1, D), D == '$fd_dom_empty'")]
    public void ADomainIsATerm(string goal) => Holds(goal);

    /// <summary>The open bounds are atoms, because inf and sup do not fit a
    /// cell's payload (ADR-051 D3).</summary>
    [Theory]
    [InlineData("'$dom_new'(1, sup, D), D = '$fd_dom'(1, sup)")]
    [InlineData("'$dom_new'(inf, 9, D), D = '$fd_dom'(inf, 9)")]
    [InlineData("'$dom_universal'(D), D = '$fd_dom'(inf, sup)")]
    public void OpenBoundsAreAtoms(string goal) => Holds(goal);

    /// <summary>Being a term, it goes through the machinery terms go through.
    /// A Foreign cell survived a copy by sharing its object; this one is
    /// copied, and the copy is a domain the builtins still accept.</summary>
    [Fact]
    public void ItTravelsAsAnOrdinaryTerm()
    {
        Holds("'$dom_new'(1, 9, D), copy_term(D, C), '$dom_contains'(C, 5)");
        Holds("'$dom_new'(1, 9, D), findall(X, member(X, [D]), [C]), "
              + "'$dom_same'(C, D)");
        Holds("'$dom_new'(1, 9, D), '$dom_new'(1, 9, E), '$dom_same'(D, E)");
        Holds(@"'$dom_new'(1, 9, D), '$dom_new'(1, 8, E), \+ '$dom_same'(D, E)");
    }

    /// <summary>The bounds keep the range they had, which is a cell's integer
    /// range and not a bit more. Measured before the change and unchanged by
    /// it (ADR-051 D3); the guide documents it.</summary>
    [Fact]
    public void TheRangeIsACellsIntegerRange()
    {
        Holds("X in 1..576460752303423487, X #> 576460752303423485, "
              + "indomain(X), X =:= 576460752303423486");
        Holds("Z in -576460752303423488..0, Z #< -576460752303423486, "
              + "indomain(Z), Z =:= -576460752303423488");
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => Fd().Query("X in 1..576460752303423488.").Success);
    }

    /// <summary>A malformed term is a type error, not an answer: with a
    /// reserved functor rather than a tag, anyone can write one (ADR-051 D1).
    /// </summary>
    [Fact]
    public void AForgedDomainIsRejected()
    {
        foreach (string bad in new[] { "foo(1,9)", "'$fd_dom'(1)", "42", "[]" })
            Assert.Throws<Shumway.Core.PrologRuntimeException>(
                () => Fd().Query($"'$dom_contains'({bad}, 5).").Success);
    }

    /// <summary>And the solver still solves. Small, because the real oracle
    /// is the clpfd suite; here to keep this file honest about what it is
    /// testing on top of a representation.</summary>
    [Fact]
    public void TheSolverStillSolves()
    {
        Holds("X in 1..9, X #> 7, X #< 9, indomain(X), X =:= 8");
        Holds("Qs = [A,B,C,Dv], Qs ins 1..4, all_different(Qs), "
              + "A #< B, B #< C, C #< Dv, label(Qs), Qs == [1,2,3,4]");
        Holds("length(Qs, 6), Qs ins 1..6, all_different(Qs), "
              + "findall(Qs, label(Qs), L), length(L, 720)");
    }
}

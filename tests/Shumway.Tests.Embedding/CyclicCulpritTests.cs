using Shumway.Core;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Issue #71: the culprit of a cyclic-list type_error was a fresh
/// variable, and a variable can never be what a type_error is about. The
/// culprit is now the argument itself, and — since the ball used to lose
/// every cycle crossing the AST leg — TermReader/Materializer carry cycle
/// knots (CycleId / IsCycleBack) so a ball round-trips as the rational tree
/// it is: for throw/1 as much as for builtin errors.</summary>
public class CyclicCulpritTests
{
    private static PrologEngine NewEngine()
        => new() { Out = new System.IO.StringWriter() };

    private static TopLevelSession NewSession()
        => new(new PrologEngine { Out = new System.IO.StringWriter() });

    [Fact]
    public void Issue71_TheCulpritIsTheCyclicArgumentItself()
    {
        // UWN's query. S == L is the whole point: the error is about the
        // very term the caller passed, cycle included.
        var e = NewEngine();
        Assert.True(e.Query(
            "L = ['1'|L], catch(number_chars(N, L), error(type_error(list, S), _), true), S == L.")
            .Success);
    }

    [Fact]
    public void NumberCodes_SameRule()
    {
        var e = NewEngine();
        Assert.True(e.Query(
            "L = [0'1|L], catch(number_codes(N, L), error(type_error(list, S), _), true), S == L.")
            .Success);
    }

    [Fact]
    public void AThrownCyclicTermKeepsItsCycle()
    {
        // ISO 7.8.10's ball is a copy, and copying a rational tree yields
        // that rational tree — the AST leg used to cut it to a fresh var.
        var e = NewEngine();
        Assert.True(e.Query(
            "L = [a|L], catch(throw(my(L)), my(B), true), B == L.").Success);
    }

    [Fact]
    public void ACyclicCompoundCulpritSurvivesToo()
    {
        var e = NewEngine();
        Assert.True(e.Query(
            "X = f(X), catch(atom_length(X, _), error(type_error(atom, C), _), true), C == X.")
            .Success);
    }

    [Fact]
    public void InteriorCycleDisplaysAsANamedEquation()
    {
        // The answer idiom a root cycle gets for free (`L = ['1'|L]`),
        // extended to a cycle buried inside a value: the owner gets a
        // synthetic name and its own line.
        using var run = NewSession().StartQuery(
            "L = ['1'|L], catch(number_chars(N, L), error(E, _), true).");
        Assert.True(run.MoveNext());
        string s = run.Format(200);
        Assert.Contains("E = type_error(list, _S1)", s);
        Assert.Contains("_S1 = ['1' | _S1]", s);
        Assert.DoesNotContain("_C", s);
    }

    [Fact]
    public void AnInteriorCycleRootedAtAUserVariableChainsByItsName()
    {
        using var run = NewSession().StartQuery("L = ['1'|L], E = t(L).");
        Assert.True(run.MoveNext());
        string s = run.Format(200);
        Assert.Contains("L = ['1' | L]", s);
        Assert.Contains("E = t(L)", s);
    }

    [Fact]
    public void RootCycleDisplayIsUnchanged()
    {
        using var run = NewSession().StartQuery("X = f(X).");
        Assert.True(run.MoveNext());
        Assert.Equal("X = f(X)", run.Format(200));
    }

    [Fact]
    public void TheUncaughtMessageElidesTheCycle_NeverAMarker()
    {
        // One line has no room for the named-equation idiom; the infinite
        // tail shows as `...` and the internal _C marker never leaks.
        var e = NewEngine();
        var re = Assert.Throws<PrologRuntimeException>(
            () => e.Query("L = ['1'|L], number_chars(N, L)."));
        string msg = ErrorRendering.FormatRuntimeError(re);
        Assert.Contains("type_error(list, ['1' | ...])", msg);
        Assert.DoesNotContain("_C", msg);
    }
}

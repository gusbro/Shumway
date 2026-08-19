using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Expanded <c>current_prolog_flag/2</c> coverage. Pre-fix only
/// <c>double_quotes</c> was readable; this lands the ISO / SWI
/// staples Prolog programs typically probe: <c>argv</c>,
/// <c>dialect</c>, <c>bounded</c>, <c>integer_rounding_function</c>,
/// <c>unknown</c>, <c>occurs_check</c>, <c>max_arity</c>.
/// </summary>
public class PrologFlagsExpandedTests
{
    [Fact]
    public void Argv_DefaultsToEmptyList()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(argv, A).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("[]"), sol["A"]);
    }

    [Fact]
    public void Argv_PopulatedByHost_ReadAsList()
    {
        var e = new PrologEngine();
        e.Flags.Argv = new[] { "foo", "bar", "baz" };
        // Pattern-match the resulting list of atoms.
        var sol = e.Query("current_prolog_flag(argv, [A, B, C]).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("foo"), sol["A"]);
        Assert.Equal(new AtomTerm("bar"), sol["B"]);
        Assert.Equal(new AtomTerm("baz"), sol["C"]);
    }

    [Fact]
    public void Argv_SingleElement()
    {
        var e = new PrologEngine();
        e.Flags.Argv = new[] { "only" };
        var sol = e.Query("current_prolog_flag(argv, [X]).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("only"), sol["X"]);
    }

    [Fact]
    public void Dialect_IsShumway()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(dialect, D).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("shumway"), sol["D"]);
    }

    [Fact]
    public void Bounded_IsFalse()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(bounded, B).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("false"), sol["B"]);
    }

    [Fact]
    public void IntegerRoundingFunction_IsTowardZero()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(integer_rounding_function, R).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("toward_zero"), sol["R"]);
    }

    [Fact]
    public void Unknown_DefaultsToError()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(unknown, U).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("error"), sol["U"]);
    }

    [Fact]
    public void OccursCheck_DefaultsToFalse()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(occurs_check, O).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("false"), sol["O"]);
    }

    [Fact]
    public void MaxArity_IsAnInteger()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(max_arity, M).");
        Assert.True(sol.Success);
        var m = Assert.IsType<IntTerm>(sol["M"]);
        Assert.True(m.Value > 0);
    }

    [Fact]
    public void DoubleQuotes_StillWorks()
    {
        // Backwards-compat probe: the pre-existing single-flag
        // surface must keep working.
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(double_quotes, V).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void UnknownFlag_RaisesDomainError()
    {
        // §8.17.2.3: an atom naming no flag is domain_error(prolog_flag, F).
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(current_prolog_flag(no_such_flag, _), "
            + "error(domain_error(prolog_flag, no_such_flag), _), true).").Success);
    }

    [Fact]
    public void Argv_UsedInsidePrologProgram_BlintPattern()
    {
        // Approximates Blint.pl's main/0 pattern: query argv,
        // pass it through a helper, branch on its first element.
        var e = new PrologEngine();
        e.Flags.Argv = new[] { "verbose", "input.txt" };
        e.ConsultString("""
            :- public got_first/1.
            got_first(F) :- current_prolog_flag(argv, [F|_]).
            """);
        var sol = e.Query("got_first(X).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("verbose"), sol["X"]);
    }
}

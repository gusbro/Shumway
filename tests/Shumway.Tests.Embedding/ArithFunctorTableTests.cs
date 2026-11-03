using Shumway.Builtins;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-003: a row of the arithmetic functor table is written once, and a
/// dead atom's id goes to another name, so a row names an operation only for a
/// permanent name.</summary>
public sealed class ArithFunctorTableTests
{
    [Fact]
    public void AnEvaluableNameWithARowIsPermanent()
    {
        var atom = AtomTable.Intern("float_fractional_part");
        int functor = FunctorTable.Intern(atom.Id, 1);

        Assert.NotEqual(0, ArithFunctorTable.RowOf(functor));
        Assert.True(atom.IsPermanent);
    }

    [Fact]
    public void ANameThatIsNotEvaluableStaysTransient()
    {
        var atom = AtomTable.Intern("no_arithmetic_function_is_called_this");
        int functor = FunctorTable.Intern(atom.Id, 1);

        Assert.Equal(0, ArithFunctorTable.RowOf(functor));
        Assert.False(atom.IsPermanent);
    }
}

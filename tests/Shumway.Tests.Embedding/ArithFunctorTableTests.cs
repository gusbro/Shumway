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

    /// <summary>What a world stages: the rows and how many are live, from one
    /// fill, read without the lock when nothing is new. A functor interned
    /// after a read is covered by the next one, and the length never runs
    /// past the rows it came with across the growths the loop causes.</summary>
    [Fact]
    public void CurrentCoversWhatWasInternedSinceTheLastRead()
    {
        int[] first = ArithFunctorTable.Current(out _);
        int[] rows = first;
        // Until the rows have been replaced at least once, and a while after.
        for (int i = 0, after = 0; after < 100; i++)
        {
            Assert.True(i < 1_000_000, "the rows never grew");
            int id = FunctorTable.Intern(AtomTable.Intern($"arith_current_{i}").Id, 2);
            rows = ArithFunctorTable.Current(out int length);
            Assert.True(length > id, $"functor {id} past the length {length}");
            Assert.True(length <= rows.Length, $"length {length} past {rows.Length} rows");
            Assert.Equal(0, rows[id]);
            if (!ReferenceEquals(first, rows)) after++;
        }
        int max = FunctorTable.Intern(AtomTable.Intern("max", permanent: true).Id, 2);
        rows = ArithFunctorTable.Current(out _);
        Assert.NotEqual(0, rows[max]);
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

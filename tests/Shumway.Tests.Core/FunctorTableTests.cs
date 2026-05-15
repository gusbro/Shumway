using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class FunctorTableTests
{
    public FunctorTableTests() => FunctorTable.ResetForTesting();

    [Fact]
    public void Intern_SameKey_ReturnsSameId()
    {
        int a = FunctorTable.Intern(atomId: 42, arity: 2);
        int b = FunctorTable.Intern(atomId: 42, arity: 2);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Intern_DifferentKeys_ReturnDifferentIds()
    {
        int a = FunctorTable.Intern(atomId: 42, arity: 2);
        int b = FunctorTable.Intern(atomId: 42, arity: 3);   // same atom, different arity
        int c = FunctorTable.Intern(atomId: 99, arity: 2);   // different atom, same arity
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void Lookup_AfterIntern_ReturnsKey()
    {
        int id = FunctorTable.Intern(atomId: 7, arity: 5);
        var (atomId, arity) = FunctorTable.Lookup(id);
        Assert.Equal(7, atomId);
        Assert.Equal(5, arity);
    }

    [Fact]
    public void Lookup_UnknownId_Throws()
    {
        Assert.Throws<ArgumentException>(() => FunctorTable.Lookup(99999));
    }

    [Fact]
    public void TryLookup_UnknownId_ReturnsFalse()
    {
        Assert.False(FunctorTable.TryLookup(99999, out _));
    }

    [Fact]
    public void TryLookup_KnownId_ReturnsEntry()
    {
        int id = FunctorTable.Intern(atomId: 11, arity: 1);
        Assert.True(FunctorTable.TryLookup(id, out var entry));
        Assert.Equal((11, 1), entry);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Intern_NegativeArity_Throws(int arity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FunctorTable.Intern(atomId: 0, arity));
    }

    [Fact]
    public void Intern_ArityZero_Allowed()
    {
        // Constants like atom/0 are valid functors.
        int id = FunctorTable.Intern(atomId: 1, arity: 0);
        var entry = FunctorTable.Lookup(id);
        Assert.Equal(0, entry.Arity);
    }

    [Fact]
    public void Count_TracksDistinctFunctors()
    {
        Assert.Equal(0, FunctorTable.Count);
        FunctorTable.Intern(1, 2);
        FunctorTable.Intern(1, 2);   // duplicate, no growth
        Assert.Equal(1, FunctorTable.Count);
        FunctorTable.Intern(1, 3);
        Assert.Equal(2, FunctorTable.Count);
    }

    [Fact]
    public void Intern_ConcurrentCallers_SameKey_ConvergeOnOneId()
    {
        const int threadCount = 16;
        const int iterations = 200;

        var ids = new int[threadCount * iterations];
        Parallel.For(0, threadCount, t =>
        {
            for (int i = 0; i < iterations; i++)
                ids[t * iterations + i] = FunctorTable.Intern(atomId: 1234, arity: 7);
        });

        // All callers must observe a single canonical id.
        Assert.True(ids.All(id => id == ids[0]));
        // And the table holds exactly one entry for this key.
        Assert.Equal(1, FunctorTable.Count);
    }

    [Fact]
    public void Intern_ConcurrentCallers_DistinctKeys_AllUnique()
    {
        const int distinct = 500;

        var ids = new int[distinct];
        Parallel.For(0, distinct, i =>
        {
            ids[i] = FunctorTable.Intern(atomId: i, arity: 1);
        });

        Assert.Equal(distinct, ids.Distinct().Count());
        Assert.Equal(distinct, FunctorTable.Count);
    }
}

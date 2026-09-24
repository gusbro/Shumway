using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>The reverse of the functor mirror: a name and an arity give the
/// id a Str cell carries. It is what lets a module PUT a term together, and
/// the one image here that needs no funnel -- the functor table is
/// append-only and its entries never change.</summary>
public sealed class FunctorReverseTableTests
{
    [Fact]
    public void EveryInternedFunctorIsFindable()
    {
        int a = AtomTable.Intern("rev_probe", permanent: true).Id;
        int f2 = FunctorTable.Intern(a, 2);
        int f0 = FunctorTable.Intern(a, 0);
        Assert.Equal(f2, FunctorReverseTable.Lookup(a, 2));
        Assert.Equal(f0, FunctorReverseTable.Lookup(a, 0));
        // And the pair really is keyed on BOTH halves.
        Assert.NotEqual(f2, f0);
    }

    [Fact]
    public void AFunctorNobodyInternedIsNotThere()
    {
        int a = AtomTable.Intern("rev_probe_absent", permanent: true).Id;
        Assert.Equal(-1, FunctorReverseTable.Lookup(a, 7));
    }

    /// <summary>It grows, and growing re-places every row rather than
    /// appending: the slots move when the mask changes, so a table that
    /// only added the new ids would lose the old ones.</summary>
    [Fact]
    public void ItSurvivesGrowth()
    {
        var ids = new System.Collections.Generic.List<(int Atom, int Arity, int Id)>();
        for (int i = 0; i < 2000; i++)
        {
            int a = AtomTable.Intern($"rev_grow_{i}", permanent: true).Id;
            ids.Add((a, i % 8, FunctorTable.Intern(a, i % 8)));
        }
        foreach (var (a, n, id) in ids)
            Assert.Equal(id, FunctorReverseTable.Lookup(a, n));
    }

    /// <summary>And it agrees with the forward mirror in both directions,
    /// which is the property a module leans on.</summary>
    [Fact]
    public void ItAgreesWithTheForwardTable()
    {
        for (int id = 0; id < FunctorTable.IdLimit; id++)
        {
            var (atomId, arity) = FunctorTable.Lookup(id);
            int back = FunctorReverseTable.Lookup(atomId, arity);
            Assert.True(back >= 0, $"functor {id} has no reverse row");
            var (a2, n2) = FunctorTable.Lookup(back);
            Assert.Equal(atomId, a2);
            Assert.Equal(arity, n2);
        }
    }
}

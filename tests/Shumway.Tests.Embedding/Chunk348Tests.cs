using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 348 (Phase 28): the first-argument index decision is compiled to
/// inline Tier-1 IL (deref + tag test + key compares branching straight to the
/// dispatch node labels), replacing the per-call
/// <c>IlIndexedDispatch.ResolveEntryByFunctorId</c> (a per-engine dictionary
/// lookup + a runtime graph walk). These run a predicate indexed on each
/// argument shape — integer, atom, struct (functor-keyed), and list/nil —
/// through a <b>persisted IL bundle</b> (the path that bakes the switch in and,
/// for atom/struct keys, patches the ids at load), asserting the dispatch is
/// still correct. (A persisted .dll cannot call a helper in the compiler
/// assembly, so the tag read goes through <c>Cell.TagId</c> in Shumway.Core —
/// the access bug this coverage guards against.)
/// </summary>
public class Chunk348Tests
{
    private static PrologEngine LoadIl(string src)
    {
        var bundle = new Bundle(new[] { new BundleEntry("c348", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));
        return engine;
    }

    [Fact]
    public void IntegerIndexed_DispatchesByValue()
    {
        // Int node: value 0 -> clause 1, any other integer -> default (clause 2).
        var e = LoadIl(
            ":- public k/2.\n" +
            "k(0, zero) :- !.\n" +
            "k(N, nonzero) :- N > 0.\n");
        Assert.True(e.Query("k(0, zero).").Success);
        Assert.True(e.Query("k(7, nonzero).").Success);
        Assert.False(e.Query("k(7, zero).").Success);     // misdispatch would pass
    }

    [Fact]
    public void AtomIndexed_DispatchesByAtomKey()
    {
        // Atom node: a -> 1, b -> 2, anything else -> default (clause 3). The
        // atom keys are patched to runtime ids at LoadBundle.
        var e = LoadIl(
            ":- public col/2.\n" +
            "col(red, 1).\n" +
            "col(green, 2).\n" +
            "col(_, 0).\n");
        Assert.True(e.Query("col(red, 1).").Success);
        Assert.True(e.Query("col(green, 2).").Success);
        Assert.True(e.Query("col(blue, 0).").Success);    // default
        Assert.False(e.Query("col(red, 2).").Success);
    }

    [Fact]
    public void StructIndexed_DispatchesByFunctorKey()
    {
        // Struct node: functor f/1 -> clause 1, g/1 -> clause 2. Functor keys
        // are patched at LoadBundle.
        var e = LoadIl(
            ":- public sh/2.\n" +
            "sh(f(_), fstruct).\n" +
            "sh(g(_), gstruct).\n");
        Assert.True(e.Query("sh(f(1), fstruct).").Success);
        Assert.True(e.Query("sh(g(2), gstruct).").Success);
        Assert.False(e.Query("sh(f(1), gstruct).").Success);
    }

    [Fact]
    public void ListIndexed_DispatchesNilVsCons()
    {
        // Term node: the empty-list atom routes to the base clause, a cons to
        // the recursive clause — the common recursive-predicate shape.
        var e = LoadIl(
            ":- public len/2.\n" +
            "len([], 0).\n" +
            "len([_|T], N) :- len(T, M), N is M + 1.\n");
        Assert.True(e.Query("len([], 0).").Success);
        Assert.True(e.Query("len([a, b, c], 3).").Success);
        Assert.False(e.Query("len([a, b, c], 2).").Success);
    }
}

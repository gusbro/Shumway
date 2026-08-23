using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 239: composite term converters — collections, tuples,
/// key-value pairs, nullables, dictionaries. The recursive paths
/// route every element through the engine's dispatcher so a user
/// converter on the element type applies uniformly.
/// </summary>
public class Chunk239Tests
{
    private static string Canon(Term t) => t.ToString() ?? "?";

    // ---- IEnumerable<T> / List<T> / T[] ----

    [Fact]
    public void ToTerm_IntArray_BuildsProperConsList()
    {
        var engine = new PrologEngine();
        var t = engine.ToTerm(new[] { 1, 2, 3 });
        // .(1, .(2, .(3, [])))
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal(".", c.Functor);
        Assert.Equal(1L, ((IntTerm)c.Args[0]).Value);
        var inner = (CompoundTerm)c.Args[1];
        Assert.Equal(2L, ((IntTerm)inner.Args[0]).Value);
        var inner2 = (CompoundTerm)inner.Args[1];
        Assert.Equal(3L, ((IntTerm)inner2.Args[0]).Value);
        Assert.Equal("[]", ((AtomTerm)inner2.Args[1]).Name);
    }

    [Fact]
    public void ToTerm_EmptyList_IsNilAtom()
    {
        var engine = new PrologEngine();
        var t = engine.ToTerm(new int[0]);
        Assert.Equal("[]", ((AtomTerm)t).Name);
    }

    [Fact]
    public void FromTerm_IntList_RoundTrips()
    {
        var engine = new PrologEngine();
        var t = engine.ToTerm(new List<int> { 10, 20, 30 });
        var back = engine.FromTerm<List<int>>(t);
        Assert.Equal(new[] { 10, 20, 30 }, back);
    }

    [Fact]
    public void FromTerm_StringArray_RoundTrips()
    {
        var engine = new PrologEngine();
        var input = new[] { "alpha", "beta", "gamma" };
        var back = engine.FromTerm<string[]>(engine.ToTerm(input));
        Assert.Equal(input, back);
    }

    [Fact]
    public void FromTerm_ImproperList_Throws()
    {
        var engine = new PrologEngine();
        // .(1, 99) — tail is not []
        var improper = new CompoundTerm(".", new Term[]
        {
            new IntTerm(1),
            new IntTerm(99),
        });
        Assert.Throws<InvalidCastException>(
            () => engine.FromTerm<List<int>>(improper));
    }

    [Fact]
    public void Query_ResultList_DecodesViaTypedAccessor()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public xs/1.
            xs([a, b, c]).
            """);
        var sol = engine.Query("xs(L).");
        var list = sol.Get<List<string>>("L");
        Assert.Equal(new[] { "a", "b", "c" }, list);
    }

    // ---- Nested lists ----

    [Fact]
    public void List_OfLists_RoundTrips()
    {
        var engine = new PrologEngine();
        var input = new List<List<int>>
        {
            new() { 1, 2 },
            new() { 3 },
            new(),
        };
        var t = engine.ToTerm(input);
        var back = engine.FromTerm<List<List<int>>>(t);
        Assert.Equal(input.Count, back.Count);
        for (int i = 0; i < input.Count; i++)
            Assert.Equal(input[i], back[i]);
    }

    // ---- Tuples ----

    [Fact]
    public void Tuple_TwoArg_RoundTrips()
    {
        var engine = new PrologEngine();
        var input = Tuple.Create(7, "name");
        var t = engine.ToTerm(input);
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("-", c.Functor);
        var back = engine.FromTerm<Tuple<int, string>>(t);
        Assert.Equal(input, back);
    }

    [Fact]
    public void ValueTuple_TwoArg_RoundTrips()
    {
        var engine = new PrologEngine();
        (int n, string s) input = (42, "hi");
        var t = engine.ToTerm(input);
        var back = engine.FromTerm<(int n, string s)>(t);
        Assert.Equal(input, back);
    }

    [Fact]
    public void KeyValuePair_RoundTrips()
    {
        var engine = new PrologEngine();
        var input = new KeyValuePair<string, int>("count", 99);
        var back = engine.FromTerm<KeyValuePair<string, int>>(engine.ToTerm(input));
        Assert.Equal(input, back);
    }

    // ---- Nullable<T> ----

    [Fact]
    public void Nullable_HasValue_MapsToSome()
    {
        var engine = new PrologEngine();
        int? input = 42;
        var t = engine.ToTerm(input);
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("some", c.Functor);
        Assert.Equal(42L, ((IntTerm)c.Args[0]).Value);
        var back = engine.FromTerm<int?>(t);
        Assert.Equal(42, back);
    }

    [Fact]
    public void Nullable_Null_MapsToNoneAtom()
    {
        var engine = new PrologEngine();
        int? input = null;
        var t = engine.ToTerm(input);
        Assert.Equal("none", ((AtomTerm)t).Name);
        var back = engine.FromTerm<int?>(t);
        Assert.Null(back);
    }

    [Fact]
    public void Nullable_FromMalformed_Throws()
    {
        var engine = new PrologEngine();
        Assert.Throws<InvalidCastException>(
            () => engine.FromTerm<int?>(new AtomTerm("maybe")));
    }

    // ---- Dictionary<K,V> ----

    [Fact]
    public void Dictionary_RoundTrips()
    {
        var engine = new PrologEngine();
        var input = new Dictionary<string, int>
        {
            ["alice"] = 30,
            ["bob"] = 25,
        };
        var t = engine.ToTerm(input);
        var back = engine.FromTerm<Dictionary<string, int>>(t);
        Assert.Equal(input.Count, back.Count);
        Assert.Equal(30, back["alice"]);
        Assert.Equal(25, back["bob"]);
    }

    [Fact]
    public void Dictionary_BuildsListOfPairs()
    {
        var engine = new PrologEngine();
        var input = new Dictionary<string, int> { ["x"] = 1 };
        var t = engine.ToTerm(input);
        // .(-(x, 1), [])
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal(".", c.Functor);
        var pair = (CompoundTerm)c.Args[0];
        Assert.Equal("-", pair.Functor);
        // A .NET string is text as a value, so it maps to an atom (ADR-047).
        Assert.Equal("x", ((AtomTerm)pair.Args[0]).Name);
        Assert.Equal(1L, ((IntTerm)pair.Args[1]).Value);
    }

    // ---- User converter applies inside a composite ----

    public record Point(int X, int Y);

    [Fact]
    public void UserConverter_Applies_InsideList()
    {
        var engine = new PrologEngine();
        engine.RegisterConverter<Point>(
            toTerm: (e, p) => new CompoundTerm("p", new Term[]
            {
                new IntTerm(p.X), new IntTerm(p.Y),
            }),
            fromTerm: t =>
            {
                var c = (CompoundTerm)t;
                return new Point((int)((IntTerm)c.Args[0]).Value,
                                 (int)((IntTerm)c.Args[1]).Value);
            });

        var input = new List<Point> { new(1, 2), new(3, 4) };
        var back = engine.FromTerm<List<Point>>(engine.ToTerm(input));
        Assert.Equal(input, back);
    }

    // ---- IEnumerable<T> interface as target ----

    [Fact]
    public void IEnumerable_AsResultType_BackedByList()
    {
        var engine = new PrologEngine();
        var t = engine.ToTerm(new[] { 1, 2, 3 });
        var seq = engine.FromTerm<IEnumerable<int>>(t);
        Assert.Equal(new[] { 1, 2, 3 }, seq.ToArray());
    }
}

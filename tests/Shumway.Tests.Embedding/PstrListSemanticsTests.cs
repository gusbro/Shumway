using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A PSTR (packed string) IS the list it represents — the design says so
/// ("a PSTR whose tail is [] is a complete proper list") and unification has
/// always agreed. These pin the places that had drifted away from it.
/// </summary>
public class PstrListSemanticsTests
{
    private static void Holds(string query)
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(query).Success, $"Query failed: {query}");
    }

    [Fact]
    public void UnifiesWithAnInlineListPatternAndWithABoundOne()
    {
        // The two paths compile differently: an inline list pattern becomes a
        // `unify_list` run, a bound variable goes value-against-value. Only the
        // second knew about PSTRs, so the same logical unification succeeded one
        // way and failed the other.
        Holds("X = \"abc\", X = [97, 98, 99].");
        Holds("X = \"abc\", Y = [97, 98, 99], X = Y.");
        Holds("X = \"abc\", X = [H|T], H == 97, T = [98, 99].");
        // …and still fails where it must.
        var engine = new PrologEngine();
        Assert.False(engine.Query("X = \"abc\", X = [97, 98].").Success);
        Assert.False(engine.Query("X = \"abc\", X = [97, 98, 99, 100].").Success);
        Assert.False(engine.Query("X = \"abc\", X = [].").Success);
    }

    [Fact]
    public void StructuralEqualitySeesThroughTheRepresentation()
    {
        // `==` after a successful `=` must be true: they are the same term.
        Holds("X = \"abc\", Y = [97, 98, 99], X = Y, X == Y.");
        Holds("X = \"abc\", X == [97, 98, 99].");
        Assert.False(new PrologEngine().Query("X = \"abc\", X == [97, 98].").Success);
        Holds("X = \"abc\", Y = \"abc\", X == Y.");
        // A nested one, so the descent is exercised rather than the top switch.
        Holds("f(\"ab\", 1) == f([97, 98], 1).");
    }

    [Fact]
    public void StandardOrderPutsAPackedListWithTheLists()
    {
        // It had its own order class and two PSTRs always tied, so sort/2 and
        // compare/3 were wrong for any program mixing text with other terms.
        Holds("compare(=, \"abc\", [97, 98, 99]).");
        Holds("compare(<, \"abc\", [97, 98, 100]).");
        Holds("compare(>, \"abc\", [97, 98]).");
        Holds("compare(<, \"ab\", \"ac\").");
        Holds("msort([\"b\", a, \"a\"], L), L == [a, \"a\", \"b\"].");
        // Duplicates collapse only when they really are equal.
        Holds("sort([\"ab\", [97, 98]], L), L == [\"ab\"].");
    }

    [Fact]
    public void AnEmptyPackedStringIsTheEmptyList()
    {
        Holds("X = \"\", X == [].");
        Holds("compare(=, \"\", []).");
    }

    [Fact]
    public void CopyingAPartialStringKeepsItsTail()
    {
        // A partial string's tail has nowhere to live in the packed AST node,
        // so copy_term/2 and findall/3 used to drop it silently.
        Holds("atom_codes(ab, L), append(L, T, P), copy_term(P-T, _-QT), var(QT).");
        Holds("X = \"abc\", copy_term(X, Y), Y == [97, 98, 99].");
        Holds("findall(Z, member(Z, [\"ab\", \"cd\"]), R), R == [\"ab\", \"cd\"].");
    }
}

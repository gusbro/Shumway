using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-047 decision 1: no predicate may answer differently for a packed list
/// than it would for the equivalent cons list. Each of these ran the same query
/// both ways and got different answers — the packed side usually walked zero
/// elements, which fails or returns an empty result rather than raising, so the
/// wrongness was silent.
/// </summary>
public class PstrConsumerTests
{
    private static Term Atom(string n) => new AtomTerm(n);

    private static void Holds(string query)
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(query).Success, $"Query failed: {query}");
    }

    private static void Fails(string query)
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query(query).Success, $"Query unexpectedly succeeded: {query}");
    }

    // ---------- type tests ----------

    [Fact]
    public void APackedListIsAListAndACompound()
    {
        Holds("is_list(\"abc\").");
        Holds("compound(\"abc\").");
        Holds("callable(\"abc\").");
        Fails("atomic(\"abc\").");
        Fails("var(\"abc\").");
        Holds("ground(\"abc\").");
    }

    [Fact]
    public void AnEmptyPackedListIsTheAtomNil()
    {
        // Length zero carries no elements, so it denotes exactly [].
        Holds("\"\" == [].");
        Holds("atomic(\"\").");
        Holds("atom(\"\").");
        Holds("is_list(\"\").");
        Fails("compound(\"\").");
    }

    [Fact]
    public void TypedListChecksSeeTheContents()
    {
        // '$is_char_list'/'$is_code_list' answer about a list's elements, which
        // is a question about the term, not about how it is stored. SWI's
        // library(error) uses them as the fast path of must_be/2.
        Holds("'$is_char_list'(\"abc\", 3).");
        Fails("'$is_code_list'(\"abc\", _).");

        // '$is_partial_string'/1 is the fast path of must_be(chars, X) in the
        // Scryer-dialect libraries. Testing the tag made it true for a packed
        // list of CODES, so that fast path accepted a code list as chars.
        Holds("'$is_partial_string'(\"abc\").");
        Fails("'$is_partial_string'(\"abc\" = _).");
        Holds("'$is_partial_string'([a, b, c]).");
        Holds("'$is_partial_string'([a, b | _]).");
        Holds("'$is_partial_string'([]).");
        Fails("'$is_partial_string'([a, 98]).");
        Fails("'$is_partial_string'([a, _, c]).");
    }

    // ---------- list predicates ----------

    [Fact]
    public void LengthCountsAPackedList()
    {
        Holds("length(\"abc\", 3).");
        Holds("length(\"\", 0).");
    }

    [Fact]
    public void MemberNthReverseAndLastWalkAPackedList()
    {
        Holds("member(b, \"abc\").");
        Fails("member(z, \"abc\").");
        Holds("nth0(1, \"abc\", b).");
        Holds("nth1(1, \"abc\", a).");
        Holds("reverse(\"abc\", [c, b, a]).");
        Holds("last(\"abc\", c).");
        Holds("list_to_set(\"aab\", [a, b]).");
    }

    [Fact]
    public void NthEnumeratesEveryPositionOfAPackedList()
    {
        // The variable-index direction re-walks the spine on each backtrack.
        Holds("findall(I-C, nth0(I, \"abc\", C), [0-a, 1-b, 2-c]).");
    }

    [Fact]
    public void TermInspectionSeesAPackedListAsTheCompoundItIs()
    {
        // functor/3, arg/3 and =.. are how generic traversal code takes a term
        // apart; all three read a compound's parts out of consecutive heap
        // slots, and a packed list's head and tail are computed instead. They
        // used to answer type_error(compound, "abc").
        Holds("functor(\"abc\", '.', 2).");
        Holds("arg(1, \"abc\", a).");
        Holds("arg(2, \"abc\", T), T == [b, c].");
        Holds("X = \"abc\", X =.. L, L == ['.', a, [b, c]].");
        // The empty one is the atom [], which is not compound.
        Fails("functor(\"\", '.', 2).");
        Holds("functor(\"\", [], 0).");
    }

    // ---------- text consumers ----------

    [Fact]
    public void FormatTildeSPrintsAPackedList()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("with_output_to(atom(A), format(\"~s\", [\"abc\"])).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("abc"), sol["A"]);
    }

    [Fact]
    public void AtomCodesAndFriendsReadAPackedList()
    {
        Holds("atom_chars(A, \"abc\"), A == abc.");
        Holds("number_chars(N, \"42\"), N == 42.");
        Holds("atom_chars(A, [a, b, c]), A == abc.");
        // name/2 is codes-only (GNU); under the chars default the literal
        // has to be spelled as codes for it.
        Holds("name(N, [0'4, 0'2]), N == 42.");
    }

    [Fact]
    public void APackedListWorksAsAFileName()
    {
        // open/3 accepts a character list as the file name; a packed one is
        // that same list.
        Fails("catch(open(\"no_such_file_qq.txt\", read, _), error(existence_error(_, _), _), fail).");
    }

    // ---------- the writer decides by value (ADR-047 decision 7) ----------

    private static string Written(string goal)
    {
        var engine = new PrologEngine();
        var sol = engine.Query($"with_output_to(atom(A), {goal}).");
        Assert.True(sol.Success, $"Query failed: {goal}");
        return ((AtomTerm)sol["A"]!).Name;
    }

    [Fact]
    public void WriteOfAPackedListIsWriteOfTheConsList()
    {
        // It used to print "abc" — which also meant quoted, ignore_ops,
        // max_depth and numbervars were all ignored for it.
        Assert.Equal("[a,b,c]", Written("write(\"abc\")"));
        Assert.Equal(Written("write([a,b,c])"), Written("write(\"abc\")"));
        Assert.Equal("[]", Written("write(\"\")"));
    }

    [Fact]
    public void TheWriteOptionsApplyToAPackedList()
    {
        Assert.Equal("'.'(a,'.'(b,'.'(c,[])))", Written("write_canonical(\"abc\")"));
        Assert.Equal("[a,b|...]", Written("write_term(\"abc\", [max_depth(2)])"));
    }

    [Fact]
    public void PortrayTextIsOffByDefaultAndDecidesOnContent()
    {
        // Same output for the packed list and the cons list of the same
        // content: two terms that are == must print identically.
        Assert.Equal("\"abc\"", Written("write_term(\"abc\", [portray_text(true)])"));
        Assert.Equal("\"abc\"", Written("write_term([a,b,c], [portray_text(true)])"));
        // A list of small integers is not text.
        Assert.Equal("[1,2,3]", Written("write_term([1,2,3], [portray_text(true)])"));
        // Mixed chars and codes is not text either.
        Assert.Equal("[a,98]", Written("write_term([a,98], [portray_text(true)])"));
        // The empty list is the atom [].
        Assert.Equal("[]", Written("write_term(\"\", [portray_text(true)])"));
        // Off by default.
        Assert.Equal("[a,b,c]", Written("write_term(\"abc\", [])"));
    }

    [Fact]
    public void PortrayedTextEscapesSoItReadsBack()
    {
        Assert.Equal("\"a\\\"b\"",
            Written("write_term([0'a, 0'\", 0'b], [portray_text(true), quoted(true)])"));
    }
}

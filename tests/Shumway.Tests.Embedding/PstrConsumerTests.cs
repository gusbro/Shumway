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
        Holds("'$is_code_list'(\"abc\", 3).");
        Fails("'$is_char_list'(\"abc\", _).");

        // '$is_partial_string'/1 is the fast path of must_be(chars, X) in the
        // Scryer-dialect libraries. Testing the tag made it true for a packed
        // list of CODES, so that fast path accepted a code list as chars.
        Fails("'$is_partial_string'(\"abc\").");
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
        Holds("member(0'b, \"abc\").");
        Fails("member(0'z, \"abc\").");
        Holds("nth0(1, \"abc\", 0'b).");
        Holds("nth1(1, \"abc\", 0'a).");
        Holds("reverse(\"abc\", [0'c, 0'b, 0'a]).");
        Holds("last(\"abc\", 0'c).");
        Holds("list_to_set(\"aab\", [0'a, 0'b]).");
    }

    [Fact]
    public void NthEnumeratesEveryPositionOfAPackedList()
    {
        // The variable-index direction re-walks the spine on each backtrack.
        Holds("findall(I-C, nth0(I, \"abc\", C), [0-0'a, 1-0'b, 2-0'c]).");
    }

    [Fact]
    public void TermInspectionSeesAPackedListAsTheCompoundItIs()
    {
        // functor/3, arg/3 and =.. are how generic traversal code takes a term
        // apart; all three read a compound's parts out of consecutive heap
        // slots, and a packed list's head and tail are computed instead. They
        // used to answer type_error(compound, "abc").
        Holds("functor(\"abc\", '.', 2).");
        Holds("arg(1, \"abc\", 0'a).");
        Holds("arg(2, \"abc\", T), T == [0'b, 0'c].");
        Holds("X = \"abc\", X =.. L, L == ['.', 0'a, [0'b, 0'c]].");
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
        Holds("atom_codes(A, \"abc\"), A == abc.");
        Holds("number_codes(N, \"42\"), N == 42.");
        Holds("atom_chars(A, [a, b, c]), A == abc.");
        Holds("name(N, \"42\"), N == 42.");
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
        Assert.Equal("[97,98,99]", Written("write(\"abc\")"));
        Assert.Equal(Written("write([97,98,99])"), Written("write(\"abc\")"));
        Assert.Equal("[]", Written("write(\"\")"));
    }

    [Fact]
    public void TheWriteOptionsApplyToAPackedList()
    {
        Assert.Equal("'.'(97,'.'(98,'.'(99,[])))", Written("write_canonical(\"abc\")"));
        Assert.Equal("[97,98|...]", Written("write_term(\"abc\", [max_depth(2)])"));
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
        Assert.Equal("[97,98,99]", Written("write_term(\"abc\", [])"));
    }

    [Fact]
    public void PortrayedTextEscapesSoItReadsBack()
    {
        Assert.Equal("\"a\\\"b\"",
            Written("write_term([0'a, 0'\", 0'b], [portray_text(true), quoted(true)])"));
    }
}

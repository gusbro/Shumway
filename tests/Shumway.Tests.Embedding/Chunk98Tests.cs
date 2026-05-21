using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 98 (Phase 7): the remaining common-library predicates —
/// control (<c>once/1</c>, <c>ignore/1</c>, <c>apply/2</c>),
/// <c>tab/1</c>, <c>findall/4</c>, and the database / inspection family
/// (<c>retractall/1</c>, <c>listing/0</c>, <c>listing/1</c>,
/// <c>format_to_atom/3</c>) — plus structural equality on floats.
/// </summary>
public class Chunk98Tests
{
    private static bool Holds(string query) => new PrologEngine().Query(query).Success;

    // ---- ==/2 on floats ----

    [Fact]
    public void StructuralEquality_HandlesFloats()
    {
        Assert.True(Holds("3.5 == 3.5."));
        Assert.False(Holds("3.5 == 3.6."));
        Assert.True(Holds("3.5 \\== 3.6."));
        Assert.True(Holds("X = 1.25, Y = 1.25, X == Y."));
        Assert.True(Holds("f(2.5, a) == f(2.5, a)."));
    }

    // ---- once / ignore ----

    [Fact]
    public void Once_CommitsToTheFirstSolution()
    {
        Assert.Single(new PrologEngine().QueryAll("once(member(X, [1,2,3]))."));
        Assert.True(Holds("once(member(X, [1,2,3])), X == 1."));
        Assert.False(Holds("once(fail)."));
    }

    [Fact]
    public void Ignore_SucceedsRegardlessOfTheGoal()
    {
        Assert.True(Holds("ignore(fail)."));
        Assert.True(Holds("ignore(true)."));
        Assert.True(Holds("ignore(member(X, [7,8])), X == 7."));
    }

    // ---- apply ----

    [Fact]
    public void Apply_AppendsTheExtraArguments()
    {
        Assert.True(Holds("apply(plus(1,2), [X]), X == 3."));
        Assert.True(Holds("apply(atom_length(hello), [N]), N == 5."));
    }

    // ---- tab ----

    [Fact]
    public void Tab_WritesSpaces()
    {
        Assert.True(Holds("with_output_to(atom(A), tab(3)), A == '   '."));
        Assert.True(Holds("with_output_to(atom(A), tab(0)), A == ''."));
    }

    // ---- findall/4 ----

    [Fact]
    public void Findall4_ProducesADifferenceList()
    {
        Assert.True(Holds(
            "findall(X, member(X, [1,2,3]), L, [end]), L == [1,2,3,end]."));
        Assert.True(Holds("findall(X, fail, L, tail), L == tail."));
    }

    // ---- format_to_atom ----

    [Fact]
    public void FormatToAtom_CapturesFormattedOutput()
    {
        Assert.True(Holds("format_to_atom(A, '~w + ~w', [1, 2]), A == '1 + 2'."));
    }

    // ---- retractall ----

    [Fact]
    public void Retractall_RemovesEveryMatchingClause()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic foo/1.\nfoo(1).\nfoo(2).\nfoo(3).");
        Assert.True(engine.Query("retractall(foo(_)).").Success);
        Assert.False(engine.Query("foo(_).").Success);
    }

    [Fact]
    public void Retractall_RemovesOnlyTheUnifyingClauses()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic foo/1.\nfoo(1).\nfoo(2).\nfoo(3).");
        Assert.True(engine.Query("retractall(foo(2)).").Success);
        Assert.True(engine.Query("foo(1).").Success);
        Assert.False(engine.Query("foo(2).").Success);
        Assert.True(engine.Query("foo(3).").Success);
    }

    [Fact]
    public void Retractall_OnAnEmptyDynamicPredicate_Succeeds()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic gone/1.");
        Assert.True(engine.Query("retractall(gone(_)).").Success);
    }

    // ---- listing ----

    [Fact]
    public void Listing1_ListsAPredicatesFacts()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic item/1.\nitem(apple).\nitem(pear).");
        Assert.True(engine.Query(
            "with_output_to(atom(A), listing(item/1)), " +
            "sub_atom(A, _, _, _, 'item(apple).'), " +
            "sub_atom(A, _, _, _, 'item(pear).').").Success);
    }

    [Fact]
    public void Listing1_ListsARuleWithItsBody()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic positive/1.\npositive(X) :- X > 0.");
        Assert.True(engine.Query(
            "with_output_to(atom(A), listing(positive/1)), " +
            "sub_atom(A, _, _, _, ':-').").Success);
    }

    [Fact]
    public void Listing1_AcceptsABarePredicateName()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic item/1.\nitem(apple).");
        Assert.True(engine.Query(
            "with_output_to(atom(A), listing(item)), " +
            "sub_atom(A, _, _, _, 'item(apple).').").Success);
    }

    [Fact]
    public void Listing0_ListsTheDynamicPredicates()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic mark/1.\nmark(here).");
        Assert.True(engine.Query(
            "with_output_to(atom(A), listing), " +
            "sub_atom(A, _, _, _, 'mark(here).').").Success);
    }

    [Fact]
    public void Listing_OmitsBuiltinsAndStaticLibraryPredicates()
    {
        // Only dynamic predicates are listed — never builtins (append/3)
        // or static library predicates (member/2).
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic item/1.\nitem(apple).");
        var sol = engine.Query(
            "with_output_to(atom(A), listing), " +
            "sub_atom(A, _, _, _, 'item(apple).').");
        Assert.True(sol.Success);
        Assert.False(engine.Query(
            "with_output_to(atom(A), listing), sub_atom(A, _, _, _, 'append(').")
            .Success);
        Assert.False(engine.Query(
            "with_output_to(atom(A), listing), sub_atom(A, _, _, _, 'member(').")
            .Success);
    }

    [Fact]
    public void Listing_PrintsADynamicHeaderAndIndentsRuleBodies()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- dynamic step/2.\nstep(X, Y) :- X > 0, Y is X - 1.");
        Assert.True(engine.Query(
            "with_output_to(atom(A), listing(step/2)), " +
            "sub_atom(A, _, _, _, ':- dynamic step/2.'), " +
            "sub_atom(A, _, _, _, 'step(').").Success);
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A grammar rule defines the predicate its translation names —
/// <c>greeting --&gt; [hello]</c> defines <c>greeting/2</c> — but the static
/// path keeps the rule untranslated (the compiler applies the flag-aware
/// transform later), so every registry that answers "which predicates exist"
/// read the head of a <c>--&gt;</c>/2 term and missed it. The non-terminal ran
/// and was invisible to current_predicate/1, listing/1, clause/2 and
/// predicate_property/2 alike.</summary>
public class GrammarRuleVisibilityTests
{
    private const string Source = """
        greeting --> [hello], [world].
        digits([D|T]) --> [D], digits(T).
        digits([D]) --> [D].
        plain(X) :- X = 1.
        """;

    private static (PrologEngine Engine, System.IO.StringWriter Out) Loaded()
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        e.ConsultString(Source);
        return (e, w);
    }

    [Fact]
    public void ANonTerminalIsAKnownPredicate()
    {
        var (e, _) = Loaded();
        Assert.True(e.Query("current_predicate(greeting/2).").Success);
        Assert.True(e.Query("current_predicate(digits/3).").Success);
        // And it still runs, which it always did.
        Assert.True(e.Query("phrase(greeting, [hello, world]).").Success);
    }

    [Fact]
    public void ANonTerminalHasPredicateProperties()
    {
        var (e, _) = Loaded();
        Assert.True(e.Query("predicate_property(greeting(_, _), static).").Success);
        Assert.True(e.Query("predicate_property(greeting(_, _), defined).").Success);
    }

    [Fact]
    public void TheNonTerminalPropertySaysWhatItIs()
    {
        var (e, _) = Loaded();
        Assert.True(e.Query("predicate_property(greeting(_, _), non_terminal).").Success);
        Assert.True(e.Query("predicate_property(digits(_, _, _), non_terminal).").Success);
        // An ordinary predicate is not one.
        Assert.False(e.Query("predicate_property(plain(_), non_terminal).").Success);
    }

    [Fact]
    public void ListingShowsTheTranslatedClauses()
    {
        // What the predicate RUNS is what listing shows — the translated
        // clauses, as every other system does.
        var (e, w) = Loaded();
        Assert.True(e.Query("listing(greeting/2).").Success);
        string s = w.ToString();
        Assert.Contains("greeting(", s);
        Assert.Contains("hello", s);
        Assert.Contains("world", s);
        Assert.DoesNotContain("-->", s);
    }

    [Fact]
    public void ListingAcceptsTheNonTerminalIndicator()
    {
        // A non-terminal is named the way it is written.
        var (e, w) = Loaded();
        Assert.True(e.Query("listing(greeting//0).").Success);
        Assert.Contains("greeting(", w.ToString());
        Assert.DoesNotContain("no predicate matches", w.ToString());
    }

    [Fact]
    public void ListingANonTerminalWithArgumentsWorksToo()
    {
        var (e, w) = Loaded();
        Assert.True(e.Query("listing(digits//1).").Success);
        string s = w.ToString();
        Assert.Contains("digits(", s);
        Assert.DoesNotContain("no predicate matches", s);
    }

    [Fact]
    public void ListingAllIncludesNonTerminals()
    {
        var (e, w) = Loaded();
        Assert.True(e.Query("listing.").Success);
        Assert.Contains("greeting(", w.ToString());
    }

    [Fact]
    public void ClauseTreatsItLikeAnyOtherStaticPredicate()
    {
        // Not a grammar-rule question: clause/2 on a static procedure is a
        // permission_error here whatever defined it, and a non-terminal is
        // now consistent with the rest instead of failing silently.
        var (e, _) = Loaded();
        Assert.True(e.Query(
            "catch(clause(greeting(_, _), _), error(E, _), true), "
            + "E = permission_error(access, private_procedure, greeting/2).").Success);
        Assert.True(e.Query(
            "catch(clause(plain(_), _), error(E, _), true), "
            + "E = permission_error(access, private_procedure, plain/1).").Success);
    }
}

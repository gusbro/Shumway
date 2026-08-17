using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 58: <c>set_prolog_flag/2</c> + <c>current_prolog_flag/2</c>
/// for the <c>double_quotes</c> flag, plus verification that the
/// DCG transform handles disjunction, if-then-else, and <c>\+/1</c>
/// in rule bodies. The DCG control-structure side was already in
/// <see cref="DcgTransform"/> from an earlier chunk; this just pins
/// the behaviour with end-to-end tests so future refactors can't
/// silently regress it.
/// </summary>
public class Chunk58Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ============================================================================
    // double_quotes flag (parser-level)
    // ============================================================================

    [Fact]
    public void DoubleQuotes_DefaultsToString()
    {
        var engine = new PrologEngine();
        Assert.Equal(DoubleQuotesMode.String, engine.Flags.DoubleQuotes);
    }

    [Fact]
    public void SetPrologFlag_DoubleQuotesCodes_ChangesEngineFlag()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("set_prolog_flag(double_quotes, codes).").Success);
        Assert.Equal(DoubleQuotesMode.Codes, engine.Flags.DoubleQuotes);
    }

    [Fact]
    public void CurrentPrologFlag_RoundTripsValue()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("set_prolog_flag(double_quotes, chars).").Success);
        var sol = engine.Query("current_prolog_flag(double_quotes, V).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("chars"), sol["V"]);
    }

    [Fact]
    public void DoubleQuotes_CodesMode_ParsesStringAsIntList()
    {
        var engine = new PrologEngine();
        engine.Query("set_prolog_flag(double_quotes, codes).");
        // "abc" now parses to [97, 98, 99]. We can verify by unifying.
        var sol = engine.Query("\"abc\" = [97, 98, 99].");
        Assert.True(sol.Success);
    }

    [Fact]
    public void DoubleQuotes_CharsMode_ParsesStringAsAtomList()
    {
        var engine = new PrologEngine();
        engine.Query("set_prolog_flag(double_quotes, chars).");
        var sol = engine.Query("\"abc\" = [a, b, c].");
        Assert.True(sol.Success);
    }

    [Fact]
    public void DoubleQuotes_AtomMode_ParsesStringAsAtom()
    {
        var engine = new PrologEngine();
        engine.Query("set_prolog_flag(double_quotes, atom).");
        var sol = engine.Query("\"hello\" = X.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["X"]);
    }

    [Fact]
    public void SetPrologFlag_AsDirective_TakesEffectMidSource()
    {
        // The directive must take effect during parse so subsequent
        // clauses in the same source see the new value.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- set_prolog_flag(double_quotes, codes).
            :- public greeting/1.
            greeting("hi").
            """);
        // greeting(_) should have been compiled with "hi" parsed as
        // [104, 105] (= [`h`, `i`] codes).
        var sol = engine.Query("greeting([104, 105]).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void SetPrologFlag_UnknownFlag_RaisesDomainError()
    {
        var engine = new PrologEngine();
        Assert.Throws<ShumwayPrologException>(
            () => engine.Query("set_prolog_flag(no_such_flag, x)."));
    }

    [Fact]
    public void SetPrologFlag_BadValue_RaisesDomainError()
    {
        var engine = new PrologEngine();
        Assert.Throws<ShumwayPrologException>(
            () => engine.Query("set_prolog_flag(double_quotes, foo)."));
    }

    // ============================================================================
    // DCG control structures (disjunction, if-then-else, \+)
    // ============================================================================

    [Fact]
    public void Dcg_Disjunction_ParsesEitherBranch()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public ab_or_xy/2.
            ab_or_xy --> ([a], [b]) ; ([x], [y]).
            """);
        Assert.True(engine.Query("ab_or_xy([a, b], []).").Success);
        Assert.True(engine.Query("ab_or_xy([x, y], []).").Success);
        Assert.False(engine.Query("ab_or_xy([a, y], []).").Success);
    }

    [Fact]
    public void Dcg_IfThenElse_PicksThenBranchOnSuccess()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public choose/2.
            choose --> ([a] -> [b] ; [c]).
            """);
        // Input [a, b]: cond [a] succeeds, then [b] required → success.
        Assert.True(engine.Query("choose([a, b], []).").Success);
        // Input [c]: cond [a] fails, falls to else [c] → success.
        Assert.True(engine.Query("choose([c], []).").Success);
        // Input [a, c]: cond [a] succeeds, commits to [b], but next is c → fail.
        Assert.False(engine.Query("choose([a, c], []).").Success);
    }

    [Fact]
    public void Dcg_Negation_SucceedsWhenLookaheadDoesNotMatch()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public not_a/2.\n" +
            // not_a consumes one element that is NOT a.
            "not_a --> \\+ [a], [_].\n");
        Assert.True(engine.Query("not_a([b], []).").Success);
        Assert.False(engine.Query("not_a([a], []).").Success);
    }
}

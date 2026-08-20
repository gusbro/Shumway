using Shumway.Compiler.Parsing;
using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-047 decisions 3 and 4: <c>double_quotes</c> is a PARSE-TIME flag that
/// decides only what the list's elements are, and the default is <c>chars</c>.
/// Whatever it selects, the literal is stored packed — the flag stopped being a
/// choice about cost when packing became available in every mode.
/// </summary>
public class DoubleQuotesFlagTests
{
    private static PrologEngine Consulted(string source)
    {
        var e = new PrologEngine();
        e.ConsultString(source);
        return e;
    }

    [Fact]
    public void TheDefaultIsChars()
    {
        var e = new PrologEngine();
        Assert.Equal(DoubleQuotesMode.Chars, e.Flags.DoubleQuotes);
        Assert.True(e.Query("X = \"abc\", X == [a, b, c].").Success);
    }

    [Fact]
    public void EveryModeDenotesWhatItSays()
    {
        var e = Consulted("""
            :- set_prolog_flag(double_quotes, codes).
            c(X) :- X = "abc".
            :- set_prolog_flag(double_quotes, chars).
            h(X) :- X = "abc".
            :- set_prolog_flag(double_quotes, atom).
            a(X) :- X = "abc".
            :- set_prolog_flag(double_quotes, string).
            s(X) :- X = "abc".
            """);
        Assert.True(e.Query("c(X), X == [0'a, 0'b, 0'c].").Success);
        Assert.True(e.Query("h(X), X == [a, b, c].").Success);
        Assert.True(e.Query("a(X), X == abc.").Success);
        // `string` is a compatibility alias for chars, not a separate type.
        Assert.True(e.Query("s(X), X == [a, b, c].").Success);
        // …and the two list modes denote DIFFERENT lists (decision 2).
        Assert.False(e.Query("c(X), h(Y), X == Y.").Success);
    }

    [Fact]
    public void ChangingTheFlagCannotReinterpretATermThatAlreadyExists()
    {
        // Decision 3. The flag is read once, when the literal is read; a term
        // carries its own presentation from then on. Under the model this
        // corrects, X below would have changed meaning under the program's feet.
        var e = Consulted("""
            :- set_prolog_flag(double_quotes, codes).
            made(X) :- X = "ab".
            """);
        Assert.True(e.Query(
            "made(X), set_prolog_flag(double_quotes, chars), X == [0'a, 0'b].").Success);
    }

    [Fact]
    public void TheFlagValueRoundTripsThroughCurrentPrologFlag()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "set_prolog_flag(double_quotes, string), "
            + "current_prolog_flag(double_quotes, string).").Success);
        Assert.True(e.Query(
            "set_prolog_flag(double_quotes, codes), "
            + "current_prolog_flag(double_quotes, codes).").Success);
    }

    [Fact]
    public void ArityCompatSelectsCodes()
    {
        // Arity's double-quoted literals are code lists, and the engine default
        // is now chars — so the dialect has to say so. Its DCGs pack just the
        // same, since both presentations are packed.
        var e = new PrologEngine();
        Assert.True(e.Query("set_prolog_flag(arity_compat, true), "
                            + "current_prolog_flag(double_quotes, codes).").Success);
    }

    [Fact]
    public void ADcgTerminalMatchesTheLiteralsOwnPresentation()
    {
        // The terminal used to expand to codes whatever the literal was, so a
        // grammar written under `chars` silently matched nothing.
        var chars = Consulted("""
            :- set_prolog_flag(double_quotes, chars).
            ab --> "ab".
            """);
        Assert.True(chars.Query("phrase(ab, [a, b]).").Success);
        Assert.False(chars.Query("phrase(ab, [0'a, 0'b]).").Success);

        var codes = Consulted("""
            :- set_prolog_flag(double_quotes, codes).
            ab --> "ab".
            """);
        Assert.True(codes.Query("phrase(ab, [0'a, 0'b]).").Success);
        Assert.False(codes.Query("phrase(ab, [a, b]).").Success);
    }

    [Fact]
    public void ALiteralIsPackedInEveryMode()
    {
        // The point of the flip: the default stopped being a cost decision.
        // A 300-character literal fits in far fewer cells than the 601 a cons
        // list would need, in either presentation.
        foreach (string mode in new[] { "codes", "chars" })
        {
            var e = new PrologEngine();
            e.ConsultString($$"""
                :- set_prolog_flag(double_quotes, {{mode}}).
                lit(X) :- X = "{{new string('a', 300)}}".
                """);
            var sol = e.Query("lit(X), length(X, N).");
            Assert.True(sol.Success);
            Assert.Equal(300L, ((Shumway.Compiler.Ast.IntTerm)sol["N"]!).Value);
        }
    }
}

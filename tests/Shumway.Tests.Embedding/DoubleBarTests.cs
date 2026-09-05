using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The double bar: <c>"abc"||T</c> is the partial list
/// <c>[a,b,c|T]</c>, both to read and to write.
///
/// <para>A grammar's answer is a difference list, and there was no way to say
/// one: the text notation only ever meant a CLOSED list, so an answer holding
/// a long text with an open tail had to spell out every character, one line
/// per element, burying the one thing the reader is after, which is where the
/// text ends and the tail begins.</para></summary>
public sealed class DoubleBarTests
{
    private static PrologEngine NewEngine() => new();

    private static string Written(string goal)
    {
        var engine = NewEngine();
        var sol = engine.Query($"with_output_to(atom(A), {goal}).");
        Assert.True(sol.Success, $"Query failed: {goal}");
        return ((AtomTerm)sol["A"]!).Name;
    }

    private static void Holds(string goal)
        => Assert.True(NewEngine().Query(goal).Success, goal);

    // ---- reading ----

    [Fact]
    public void TheBarsPrependTheTextToTheTail()
    {
        Holds(@"X = ""abc""||K, X == [a,b,c|K].");
    }

    [Fact]
    public void TheTailCanBeAnything()
    {
        // Any primary, since that is what the notation takes: a list, an atom,
        // another text.
        Holds(@"X = ""ab""||[c,d], X == [a,b,c,d].");
        Holds(@"X = ""ab""||c, X == [a,b|c].");
        Holds(@"X = ""a""||""b""||""c"", X == [a,b,c].");
    }

    [Fact]
    public void AnEmptyTextPrependsNothing()
    {
        Holds(@"X = """"||K, X == K.");
        Holds(@"X = """"||[a], X == [a].");
    }

    [Fact]
    public void ItBindsTighterThanEveryOperator()
    {
        // The tail is a term of priority 0, so `X = "ab"||T` needs no
        // parentheses and the bars never reach across an operator.
        Holds(@"X = ""ab""||T, T = [], X == [a,b].");
        Holds(@"X = f(""ab""||T, 1), arg(1, X, [a,b|T]).");
        Holds(@"X = [""a""||T], X == [[a|T]].");
    }

    [Fact]
    public void ItAttachesToTheLiteralAndToNothingElse()
    {
        // What precedes the bars has to be the double-quoted token itself:
        // parenthesised, it is an ordinary term again and the bars are the
        // syntax error they always were.
        Holds(@"catch(read_term_from_atom('(""a"")||[]', _, []),
                      error(syntax_error(_), _), true).");
        Holds(@"catch(read_term_from_atom('foo||[]', _, []),
                      error(syntax_error(_), _), true).");
    }

    [Fact]
    public void ItFollowsTheFlagThatSaysWhatATextIs()
    {
        var e = NewEngine();
        Assert.True(e.Query("set_prolog_flag(double_quotes, codes).").Success);
        Assert.True(e.Query(@"X = ""ab""||K, X == [0'a,0'b|K].").Success);

        // ...and where the flag leaves no list to open, the bars mean what
        // they meant before, which is nothing.
        var atoms = NewEngine();
        Assert.True(atoms.Query("set_prolog_flag(double_quotes, atom).").Success);
        Assert.True(atoms.Query(
            @"catch(read_term_from_atom('""ab""||K', _, []),
                    error(syntax_error(_), _), true).").Success);
    }

    // ---- writing ----

    [Fact]
    public void AnOpenTextWritesWithTheBars()
    {
        Assert.Matches(@"^""ab""\|\|_", Written(@"write_term([a,b|_T], [double_quotes(true)])"));
        Assert.Matches(@"^""ab""\|\|_",
                       Written("write_term([0'a,0'b|_T], [double_quotes(true)])"));
    }

    [Fact]
    public void AClosedOneIsUnchanged()
    {
        Assert.Equal(@"""abc""", Written(@"write_term([a,b,c], [double_quotes(true)])"));
        Assert.Equal("[]", Written(@"write_term([], [double_quotes(true)])"));
    }

    [Fact]
    public void WhatIsNotTextIsUnchangedToo()
    {
        // Codes no text notation covers, and a bare variable.
        Assert.Matches(@"^\[1,2\|_", Written("write_term([1,2|_T], [double_quotes(true)])"));
        Assert.Matches("^_", Written("write_term(_T, [double_quotes(true)])"));
    }

    [Fact]
    public void TheOptionIsItsOwn()
    {
        // portray_text says a list is text; the bars are the separate
        // question of how an OPEN one is written.
        Assert.Matches(@"^\[a,b\|_", Written("write_term([a,b|_T], [portray_text(true)])"));
        Assert.Matches(@"^""ab""\|\|_",
                       Written("write_term([a,b|_T], [portray_text(true), double_bar(true)])"));
        Assert.Matches(@"^\[a,b\|_",
                       Written("write_term([a,b|_T], [double_quotes(true), double_bar(false)])"));
    }

    [Fact]
    public void TheTextIsQuotedTheWayItIsRead()
    {
        Assert.Matches(@"^""a\\""b""\|\|_",
                       Written(@"write_term(['a','""','b'|_T], [double_quotes(true)])"));
        Assert.Matches(@"^""a\\nb""\|\|_",
                       Written(@"write_term(['a','\n','b'|_T], [double_quotes(true)])"));
    }

    [Fact]
    public void WhatIsWrittenIsWhatIsRead()
    {
        // The point of the notation: the answer can be pasted back in.
        Holds(@"X = [a,b|c],
                with_output_to(atom(A), write_term(X, [double_quotes(true)])),
                read_term_from_atom(A, Y, []),
                X == Y.");
        Holds(@"X = ['a','""'|c],
                with_output_to(atom(A), write_term(X, [double_quotes(true), quoted(true)])),
                read_term_from_atom(A, Y, []),
                X == Y.");
    }

    // ---- the answer ----

    [Fact]
    public void AnAnswerSaysWhereTheTextEnds()
    {
        // The case the proposal is about: a grammar leaves the tail open and
        // the answer used to be one line per character.
        var e = NewEngine();
        var session = new TopLevelSession(e);
        using var run = session.StartQuery(@"phrase(""a text"", S0, S).");
        Assert.True(run.MoveNext());
        Assert.Equal(@"S0 = ""a text""||S", run.Format(width: 200));
    }

    [Fact]
    public void AClosedAnswerIsUnchanged()
    {
        var e = NewEngine();
        var session = new TopLevelSession(e);
        using var run = session.StartQuery(@"X = ""ab"", Y = [1,2|_T].");
        Assert.True(run.MoveNext());
        string s = run.Format(width: 200);
        Assert.Contains(@"X = ""ab""", s);
        Assert.Contains("Y = [1, 2 | ", s);
    }
}

using System;
using System.Linq;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The editor's highlighting, which runs the ENGINE'S lexer rather than a
/// separate pattern language — so these pin the property that motivates that
/// choice: the colours agree with how the reader actually reads the text
/// (quoted atoms, 0'c, block comments, program-declared operators), and a
/// half-typed buffer degrades instead of throwing.
/// </summary>
public class SyntaxHighlighterTests
{
    private static (string Text, SpanKind Kind)[] Spans(string source, PrologEngine? engine = null)
        => SyntaxHighlighter.Highlight(source, engine?.Operators)
            .Select(s => (source.Substring(s.Start, s.Length), s.Kind))
            .ToArray();

    /// <summary>Every test asserts this: the spans reproduce the source exactly.
    /// A highlighter that loses or duplicates a character corrupts the overlay
    /// it drives, which is worse than colouring something wrong.</summary>
    private static void AssertCovers(string source, PrologEngine? engine = null)
    {
        var spans = SyntaxHighlighter.Highlight(source, engine?.Operators);
        int at = 0;
        foreach (var s in spans)
        {
            Assert.Equal(at, s.Start);
            Assert.True(s.Length > 0, "empty span");
            at += s.Length;
        }
        Assert.Equal(source.Length, at);
    }

    [Theory]
    [InlineData("foo(X) :- bar(X).")]
    [InlineData("% just a comment")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a :- b, c ; d -> e.")]
    [InlineData("X = 'quoted atom', Y = \"a string\".")]
    [InlineData("N is 0'a + 16'ff + 1.5e10.")]
    [InlineData("/* block */ p. /* unterminated")]
    [InlineData("p('unterminated quote")]
    [InlineData("[a, b | T]")]
    [InlineData("\n\n\t p.\n")]
    public void SpansCoverTheSourceExactly(string source) => AssertCovers(source);

    [Fact]
    public void ClassifiesTheBasicShapes()
    {
        var spans = Spans("foo(Bar, 42).");
        Assert.Contains(("foo", SpanKind.Atom), spans);
        Assert.Contains(("Bar", SpanKind.Variable), spans);
        Assert.Contains(("42", SpanKind.Number), spans);
        Assert.Contains(("(", SpanKind.Punctuation), spans);
    }

    [Fact]
    public void CommentsAreRecoveredFromTheGapsTheLexerSkips()
    {
        // The lexer does not emit comments at all; they are reconstructed from
        // the space between tokens. Both forms, and a line comment that ends at
        // the newline rather than swallowing it.
        var line = Spans("p. % tail\nq.");
        Assert.Contains(("% tail", SpanKind.Comment), line);

        var block = Spans("p /* mid */ q.");
        Assert.Contains(("/* mid */", SpanKind.Comment), block);
    }

    [Fact]
    public void QuotedAtomsReadAsQuotedEvenWhenTheyNameAnOperator()
    {
        // 'mod' is a name, not the operator — the reader does not treat it as
        // one, so neither does the colouring.
        var engine = new PrologEngine();
        var spans = Spans("X = 'mod'.", engine);
        Assert.Contains(("'mod'", SpanKind.Quoted), spans);
    }

    [Fact]
    public void OperatorsComeFromTheLiveTable()
    {
        var engine = new PrologEngine();
        // Standard operators are known from the start.
        Assert.Contains((":-", SpanKind.Operator), Spans("a :- b.", engine));
        Assert.Contains(("is", SpanKind.Operator), Spans("X is 1.", engine));

        // With no table, an operator is just an atom — the caller decides.
        Assert.Contains(("is", SpanKind.Atom), Spans("X is 1."));
    }

    [Fact]
    public void ProgramDeclaredOperatorsAreHighlighted()
    {
        // The payoff of asking the live table: a library's own operators colour
        // like operators. A fixed pattern list could not know about them.
        var engine = new PrologEngine();
        Assert.Contains(("#=", SpanKind.Atom), Spans("X #= Y.", engine));

        engine.ConsultString(":- op(700, xfx, #=).");
        Assert.Contains(("#=", SpanKind.Operator), Spans("X #= Y.", engine));
    }

    [Fact]
    public void CharacterCodesAndRadixLiteralsAreOneNumber()
    {
        Assert.Contains(("0'a", SpanKind.Number), Spans("X = 0'a."));
        Assert.Contains(("16'ff", SpanKind.Number), Spans("X = 16'ff."));
    }

    [Fact]
    public void AnEndOfClauseDotIsNotPartOfANumber()
    {
        var spans = Spans("X = 42.");
        Assert.Contains(("42", SpanKind.Number), spans);
        Assert.Contains((".", SpanKind.Punctuation), spans);
    }

    [Fact]
    public void AFloatKeepsItsDot()
    {
        Assert.Contains(("1.5", SpanKind.Number), Spans("X = 1.5."));
    }

    [Fact]
    public void HalfTypedInputDegradesInsteadOfThrowing()
    {
        // What the buffer looks like between two keystrokes. The text before the
        // problem still colours; the rest is marked and the caller carries on.
        var spans = Spans("foo(X) :- 'unterminated");
        Assert.Contains(("foo", SpanKind.Atom), spans);
        Assert.Contains(spans, s => s.Kind == SpanKind.Error);
        AssertCovers("foo(X) :- 'unterminated");
    }

    [Fact]
    public void AdjacentRunsOfTheSameKindAreMerged()
    {
        // One element per run, not per token: the overlay stays small for a
        // buffer that is mostly whitespace and comments.
        var spans = SyntaxHighlighter.Highlight("p.\n\n\n\nq.");
        Assert.DoesNotContain(spans.Zip(spans.Skip(1)),
            pair => pair.First.Kind == pair.Second.Kind);
    }
}

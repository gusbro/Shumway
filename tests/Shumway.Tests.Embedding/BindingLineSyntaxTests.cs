using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An answer line is the goal <c>Name = Value</c>, so the value is
/// an argument of <c>=</c>/2 (700 xfx) and must render bounded by 699. It was
/// rendered at 1200, so <c>X = (a,b)</c> printed as <c>X = a, b</c> — which
/// reads back as two separate bindings, and <c>X = (a:-b)</c> as something
/// that is not even a binding. The reference implementations parenthesize
/// exactly these.</summary>
public class BindingLineSyntaxTests
{
    private static string AnswerOf(string goal)
    {
        var session = new TopLevelSession(
            new PrologEngine { Out = new System.IO.StringWriter() });
        using var run = session.StartQuery(goal);
        Assert.True(run.MoveNext());
        return run.Format(200);
    }

    [Theory]
    [InlineData("X = (a,b).", "X = (a, b)")]
    [InlineData("X = (a;b).", "X = (a; b)")]
    [InlineData("X = (a->b).", "X = (a->b)")]
    [InlineData("X = (a:-b).", "X = (a:-b)")]
    [InlineData("X = (a,b,c).", "X = (a, b, c)")]
    public void AValueAboveArgumentPriorityIsParenthesised(string goal, string expected)
        => Assert.Equal(expected, AnswerOf(goal));

    [Theory]
    // Below 700 nothing changes: parenthesising those would be noise.
    [InlineData("X = 1+2.", "X = 1+2")]
    [InlineData("X = f(a,b).", "X = f(a, b)")]
    [InlineData("X = [a,b].", "X = \"ab\"")]
    [InlineData("X = 'hello world'.", "X = 'hello world'")]
    [InlineData("X = f((a,b)).", "X = f((a, b))")]
    public void AValueThatNeedsNoParenthesesKeepsNone(string goal, string expected)
        => Assert.Equal(expected, AnswerOf(goal));

    [Fact]
    public void TheAnswerLineReadsBackAsTheSameTerm()
    {
        // The point of the parentheses: the line the user is shown is a goal
        // they can paste back, and it means what it showed.
        foreach (string value in new[] { "(a,b)", "(a;b)", "(a->b)", "(a,b,c)" })
        {
            string line = AnswerOf($"X = {value}.");
            var e = new PrologEngine { Out = new System.IO.StringWriter() };
            // Reading the printed line as a term must give back an =/2 whose
            // right side is the original value.
            Assert.True(
                e.Query($"read_term_from_atom('{line}', T, []), T = (_ = V), "
                        + $"V == {value}.").Success,
                $"the printed line `{line}` does not read back as `{value}`");
        }
    }

    [Fact]
    public void ChainedBindingsParenthesiseToo()
    {
        Assert.Equal("X = Y,\nY = (a, b)", AnswerOf("X = (a,b), Y = X."));
    }
}

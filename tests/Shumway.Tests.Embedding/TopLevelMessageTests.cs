using Shumway.Core;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Top-level message accuracy (issue #65): the printed error is the
/// SAME term catch/3 unifies with, culprits render in Prolog syntax, internal
/// $-helpers never surface as the error context, and answers print quoted
/// with char lists portrayed as "..." — so a raw newline in a value can no
/// longer break the transcript's own syntax.</summary>
public class TopLevelMessageTests
{
    private static TopLevelSession NewSession()
        => new(new PrologEngine { Out = new System.IO.StringWriter() });

    [Fact]
    public void CharListAnswer_PortraysAsString()
    {
        // UWN's transcript: L = "\n" must answer L = "\n", not a raw
        // newline inside list brackets.
        using var run = NewSession().StartQuery(
            "atom_codes(A, [110, 10]), atom_chars(A, L).");
        Assert.True(run.MoveNext());
        string s = run.Format(200);
        Assert.Contains("L = \"n\\n\"", s);
    }

    [Fact]
    public void AtomsQuoteWhenTheyMustToReRead()
    {
        using var run = NewSession().StartQuery("X = 'hello world'.");
        Assert.True(run.MoveNext());
        Assert.Equal("X = 'hello world'", run.Format(200));
    }

    [Fact]
    public void CodesStayNumeric()
    {
        // [65, 66] is a list of small integers unless the PROGRAM says
        // text; dressing it up as "AB" would misreport arbitrary data.
        using var run = NewSession().StartQuery("X = [65, 66].");
        Assert.True(run.MoveNext());
        Assert.Equal("X = [65, 66]", run.Format(200));
    }

    [Fact]
    public void CaughtBallAndMessageAgree_IndicatorParenthesised()
    {
        using var run = NewSession().StartQuery("catch(1 is foo, E, true).");
        Assert.True(run.MoveNext());
        Assert.Equal(
            "E = error(type_error(evaluable, foo/0), (is)/2)", run.Format(200));
    }

    [Fact]
    public void ExistenceErrorMessage_ShowsTheFullIsoShape()
    {
        // The message renders the same ball catch/3 sees — never again the
        // one-argument existence_error(inex/0) that taught users a shape
        // no catcher would match.
        var re = new PrologRuntimeException("existence_error", "inex/0");
        Assert.Equal("existence_error(procedure, inex/0)",
            ErrorRendering.FormatRuntimeError(re));
    }

    [Fact]
    public void FloatCulprit_RendersAsAFloat()
    {
        var re = new PrologRuntimeException("type_error", "callable", (object)0.0);
        re.StampBuiltin("$type_error_callable", 1);
        // 0.0 must not print as the C#-default "0", and the $-internal
        // helper is machinery, not context the user called.
        Assert.Equal("type_error(callable, 0.0)",
            ErrorRendering.FormatRuntimeError(re));
    }

    [Fact]
    public void RealBuiltinContextStays()
    {
        var re = new PrologRuntimeException("evaluation_error", "zero_divisor");
        re.StampBuiltin("is", 2);
        Assert.Equal("evaluation_error(zero_divisor) in is/2",
            ErrorRendering.FormatRuntimeError(re));
    }
}

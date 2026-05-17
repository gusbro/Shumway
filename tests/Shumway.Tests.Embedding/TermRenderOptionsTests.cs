using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 33: write_term/2 now honours the options it's given.
/// Phase 1 supports <c>quoted/1</c>, <c>ignore_ops/1</c> (accepted but
/// no-op since the renderer already uses canonical form), and
/// <c>numbervars/1</c>.
/// </summary>
public class TermRenderOptionsTests
{
    private static PrologEngine WithCaptureOut(out StringWriter sw)
    {
        sw = new StringWriter();
        return new PrologEngine { Out = sw };
    }

    // ---------- quoted option ----------

    [Fact]
    public void WriteTerm_PlainAtom_Unquoted()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term(hello, [quoted(true)]).");
        Assert.Equal("hello", sw.ToString());
    }

    [Fact]
    public void WriteTerm_AtomWithSpecialChar_Quoted()
    {
        var engine = WithCaptureOut(out var sw);
        // 'hello world' needs quoting under quoted(true).
        engine.Query("write_term('hello world', [quoted(true)]).");
        Assert.Equal("'hello world'", sw.ToString());
    }

    [Fact]
    public void WriteTerm_QuotedFalse_UsesPlainOutput()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term('hello world', [quoted(false)]).");
        // No quoting when explicit false.
        Assert.Equal("hello world", sw.ToString());
    }

    [Fact]
    public void WriteTerm_QuotedAtomEscapesQuote()
    {
        var engine = WithCaptureOut(out var sw);
        // An atom containing a quote should escape it.
        engine.Query("write_term('it''s', [quoted(true)]).");
        Assert.Equal("'it\\'s'", sw.ToString());
    }

    [Fact]
    public void WriteTerm_NoOptions_DefaultsToUnquoted()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term('with space', []).");
        // Empty options = no quoted.
        Assert.Equal("with space", sw.ToString());
    }

    // ---------- numbervars option ----------

    [Fact]
    public void WriteTerm_Numbervars_RendersAsLetters()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query(
            "numbervars(p(X, Y, Z), 0, _), "
            + "write_term(p(X, Y, Z), [numbervars(true)]).");
        Assert.Equal("p(A, B, C)", sw.ToString());
    }

    [Fact]
    public void WriteTerm_NumbervarsWithSuffix()
    {
        var engine = WithCaptureOut(out var sw);
        // $VAR(26) → A1, $VAR(27) → B1.
        engine.Query("write_term('$VAR'(26), [numbervars(true)]).");
        Assert.Equal("A1", sw.ToString());
    }

    [Fact]
    public void WriteTerm_NumbervarsFalse_KeepsCompoundForm()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("write_term('$VAR'(0), [numbervars(false)]).");
        Assert.Equal("$VAR(0)", sw.ToString());
    }

    // ---------- combined ----------

    [Fact]
    public void WriteTerm_QuotedAndNumbervars_Combined()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query(
            "write_term(foo('weird atom', '$VAR'(0)), "
            + "[quoted(true), numbervars(true)]).");
        Assert.Equal("foo('weird atom', A)", sw.ToString());
    }

    // ---------- write/1 is unchanged ----------

    [Fact]
    public void Write_StillUnquoted()
    {
        var engine = WithCaptureOut(out var sw);
        engine.Query("write('hello world').");
        Assert.Equal("hello world", sw.ToString());
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>read/1 raises at the character that PROVES no valid token can
/// follow (issue #37, conformity s#2): a quote followed by a raw newline
/// can never be lexed, so ISO §8.14.1.1's as-if-character-by-character
/// reading decides the syntax error right there — the old reader kept
/// prompting until the user donated a closing quote and an end dot. The
/// backslash-newline continuation remains the one legal wait, and the
/// Arity exemption (raw control bytes are legit quoted text there)
/// mirrors the lexer's.</summary>
public sealed class ReadPoisonedInputTests
{
    /// <summary>Yields the given text; one more read is the failure the
    /// suite is hunting — past the poison there is NOTHING a conforming
    /// reader may consume (a file's EOF would mask exactly this).</summary>
    private sealed class GuardReader(string text) : System.IO.TextReader
    {
        private int _pos;
        private int At(bool advance)
        {
            if (_pos >= text.Length)
                throw new System.InvalidOperationException(
                    "read/1 consumed input past the poisoning character.");
            return advance ? text[_pos++] : text[_pos];
        }
        public override int Peek() => At(advance: false);
        public override int Read() => At(advance: true);
    }

    private static PrologEngine WithInput(System.IO.TextReader reader) =>
        new() { In = reader };

    [Fact]
    public void TheIssueTranscript_RaisesWithoutOverReading()
    {
        var e = WithInput(new GuardReader("             '\n"));
        Assert.True(e.Query(
            "catch(read(_), error(syntax_error(_), _), true).").Success);
    }

    [Fact]
    public void TheSentinelComesBackWhole()
    {
        // The page's dual-read form: after the error at the poisoning
        // newline, the NEXT term on the stream must arrive intact — a
        // waiting reader would have swallowed it into the quoted atom.
        var e = WithInput(new System.IO.StringReader("'\nsentinel_ok.\n"));
        Assert.True(e.Query(
            "catch(read(_), error(syntax_error(_), _), true), "
          + "read(T), T == sentinel_ok.").Success);
    }

    [Fact]
    public void TheContinuationIsTheLegalWait()
    {
        // Backslash-newline inside the quote is the line continuation:
        // the reader must keep going, and the spliced atom has no newline.
        var e = WithInput(new System.IO.StringReader("'ab\\\nc'.\n"));
        Assert.True(e.Query("read(T), T == abc.").Success);
    }

    [Fact]
    public void PoisonInsideANumericEscape()
    {
        var e = WithInput(new GuardReader("'\\x41\n"));
        Assert.True(e.Query(
            "catch(read(_), error(syntax_error(_), _), true).").Success);
    }

    [Fact]
    public void PoisonAfterZeroQuote()
    {
        var e = WithInput(new GuardReader("0'\n"));
        Assert.True(e.Query(
            "catch(read(_), error(syntax_error(_), _), true).").Success);
    }

    [Fact]
    public void StringsPoisonToo()
    {
        var e = WithInput(new GuardReader("\"ab\n"));
        Assert.True(e.Query(
            "catch(read(_), error(syntax_error(_), _), true).").Success);
    }

    [Fact]
    public void AritySourcesKeepTheirRawBytes()
    {
        // Under arity_compat the lexer accepts raw control characters in
        // quoted text, so the scanner must keep reading to the real end.
        var e = WithInput(new System.IO.StringReader("'a\nb'.\n"));
        Assert.True(e.Query("set_prolog_flag(arity_compat, true).").Success);
        Assert.True(e.Query("read(T), atom_length(T, 3).").Success);
    }
}

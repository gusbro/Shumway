using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A float literal whose syntax is perfect but whose value exceeds
/// double range (issue #42, Neumerkel number_chars #82 / stc #74): the text
/// names a value outside the float range — an implementation limit, so the
/// report is representation_error(max_float), or min_float when a UNARY
/// minus makes the literal itself negative (issue #42 follow-up: the sign
/// follows the syntax, so `0-9.9e999` stays max_float — the minus there is
/// binary and the overflowing literal is positive). Never
/// syntax_error(illegal_number) and
/// never a silently infinite float. Covers every mouth the literal can enter:
/// number_chars/number_codes, the term reader (atom_to_term, read_term,
/// consult), and the top-level parse; atom_number/2 keeps its conventional
/// fail. Underflow to 0.0 stays a plain success (case 80).</summary>
public sealed class FloatOverflowErrorTests
{
    private const string CatchMaxFloat =
        "error(representation_error(max_float), _)";
    private const string CatchMinFloat =
        "error(representation_error(min_float), _)";

    [Fact]
    public void TheIssueTranscript()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"catch(number_chars(_, \"9.9e999\"), {CatchMaxFloat}, true).").Success);
    }

    [Fact]
    public void NumberCodes_AndTheNegatedLiteral()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "atom_codes('9.9e999', Cs), "
          + $"catch(number_codes(_, Cs), {CatchMaxFloat}, true).").Success);
        Assert.True(e.Query(
            $"catch(number_chars(_, \"-9.9e999\"), {CatchMinFloat}, true).").Success);
    }

    [Fact]
    public void TheDoubleBoundary()
    {
        var e = new PrologEngine();
        // Largest double is ~1.7976931348623157e308: just under parses,
        // just over is above max_float.
        Assert.True(e.Query("number_chars(N, \"1.7e308\"), N > 0.").Success);
        Assert.True(e.Query(
            $"catch(number_chars(_, \"1.8e308\"), {CatchMaxFloat}, true).").Success);
        // Underflow is representable — it rounds to 0.0 (case 80).
        Assert.True(e.Query("number_chars(N, \"1.0e-999\"), N == 0.0.").Success);
    }

    [Fact]
    public void TheQuotedMinusFallbackReader()
    {
        // `'-' 9.9e999` is not a number TOKEN sequence — it reaches the
        // full-term-reader fallback, which must surface the same error, not
        // swallow it into "not a number" → syntax_error. The quoted minus
        // is still a UNARY minus, so the flaw is min_float.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "atom_chars('\\'-\\' 9.9e999', Cs), "
          + $"catch(number_chars(_, Cs), {CatchMinFloat}, true).").Success);
        // The fallback's ordinary cases are untouched.
        Assert.True(e.Query(
            "atom_chars('\\'-\\'1', Ds), number_chars(M, Ds), M =:= -1.").Success);
    }

    [Fact]
    public void NotANumberStaysASyntaxError()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(number_chars(_, \"abc\"), "
          + "error(syntax_error(illegal_number), _), true).").Success);
    }

    [Fact]
    public void AtomNumberKeepsItsConventionalFail()
    {
        var e = new PrologEngine();
        var sol = e.Query("atom_number('9.9e999', _).");
        Assert.False(sol.Success);
        Assert.True(e.Query("atom_number('1.5e308', N), N > 0.").Success);
    }

    [Fact]
    public void TheTermReaderRaisesToo()
    {
        // The reader used to hand back a silently infinite float.
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"catch(atom_to_term('9.9e999', _, _), {CatchMaxFloat}, true).").Success);
        Assert.True(e.Query(
            $"catch(atom_to_term('-9.9e999', _, _), {CatchMinFloat}, true).").Success);
        Assert.True(e.Query(
            "atom_to_term('1.5e308', T, _), float(T), T > 0.").Success);
    }

    [Fact]
    public void UnaryMinusIsMinFloat_BinaryMinusIsMaxFloat()
    {
        // The sign of the flaw follows the SYNTAX: `-999.0e999` is one
        // negative literal (below min_float); in `0-999.0e999` the minus is
        // binary and the positive literal overflows first, at read time —
        // which is also why these route through atom_to_term: a query
        // containing the literal never parses, so nothing could catch.
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"catch(atom_to_term('-999.0e999', _, _), {CatchMinFloat}, true).").Success);
        Assert.True(e.Query(
            $"catch(atom_to_term('0-999.0e999', _, _), {CatchMaxFloat}, true).").Success);
        // A spaced unary minus is still unary.
        Assert.True(e.Query(
            $"catch(atom_to_term('- 9.9e999', _, _), {CatchMinFloat}, true).").Success);
    }

    [Fact]
    public void ConsultingAnOverflowingLiteralRaises_NothingLoads()
    {
        var e = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => e.ConsultString("x(9.9e999).\ny(1).\n"));
        Assert.Equal("representation_error", ex.Kind);
        Assert.Equal("max_float", ex.Detail);
        // All-or-nothing: the good clause after the bad one did not load.
        Assert.True(e.Query(
            "catch(y(_), error(existence_error(_, _), _), true).").Success);
    }
}

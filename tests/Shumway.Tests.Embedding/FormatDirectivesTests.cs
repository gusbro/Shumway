using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 (Logtalk bring-up): the standard <c>format/2</c> directives
/// <c>~q ~p ~i ~e ~f ~g ~r ~R ~D</c>, surfaced by Logtalk's runtime
/// initialization message printing (a bare <c>~q</c> raised
/// <c>domain_error(format_spec)</c>).
/// </summary>
public class FormatDirectivesTests
{
    private static string FormatToAtom(string fmt, string args)
    {
        var e = new PrologEngine();
        var sol = e.Query($"format_to_atom(A, '{fmt}', {args}).");
        Assert.True(sol.Success, $"format('{fmt}', {args}) failed");
        return ((AtomTerm)sol["A"]!).Name;
    }

    [Fact] public void Q_QuotesWhenNeeded() =>
        Assert.Equal("'hello world'", FormatToAtom("~q", "['hello world']"));

    [Fact] public void P_LikeWrite() =>
        Assert.Equal("foo(1)", FormatToAtom("~p", "[foo(1)]"));

    [Fact] public void I_IgnoresArgument() =>
        Assert.Equal("ab", FormatToAtom("a~ib", "[ignored]"));

    [Fact] public void F_FixedDecimals() =>
        Assert.Equal("3.14", FormatToAtom("~2f", "[3.14159]"));

    [Fact] public void F_DefaultSixDecimals() =>
        Assert.Equal("2.500000", FormatToAtom("~f", "[2.5]"));

    [Fact] public void F_AcceptsInteger() =>
        Assert.Equal("5.00", FormatToAtom("~2f", "[5]"));

    [Fact] public void R_RadixLowercase() =>
        Assert.Equal("ff", FormatToAtom("~16r", "[255]"));

    [Fact] public void R_RadixUppercase() =>
        Assert.Equal("FF", FormatToAtom("~16R", "[255]"));

    [Fact] public void R_Binary() =>
        Assert.Equal("1010", FormatToAtom("~2r", "[10]"));

    [Fact] public void D_ThousandsSeparators() =>
        Assert.Equal("1,234,567", FormatToAtom("~D", "[1234567]"));
}

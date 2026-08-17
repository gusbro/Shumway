using System;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A <c>format/2,3</c> format string may be an atom, a list of
/// character CODES, or a list of one-char atoms — which is what
/// <c>format("...", …)</c> becomes under each <c>double_quotes</c> setting.
/// Only the atom form was accepted; the other two raised
/// <c>type_error(atom, _)</c>.</summary>
public sealed class FormatTextSpecTests
{
    private static string Captured(PrologEngine e, string goal)
    {
        var sol = e.Query($"with_output_to(atom(Out), ({goal})).");
        Assert.True(sol.Success);
        return ((AtomTerm)sol["Out"]!).Name;
    }

    [Fact]
    public void CodeListFormatSpec_Works()
    {
        var e = new PrologEngine();
        // "~w-~w" as codes.
        Assert.Equal("a-b",
            Captured(e, "format([0'~,0'w,0'-,0'~,0'w], [a,b])"));
    }

    [Fact]
    public void CharListFormatSpec_Works()
    {
        var e = new PrologEngine();
        Assert.Equal("a-b",
            Captured(e, "format(['~','w','-','~','w'], [a,b])"));
    }

    [Fact]
    public void AtomFormatSpec_StillWorks()
    {
        var e = new PrologEngine();
        Assert.Equal("a-b", Captured(e, "format('~w-~w', [a,b])"));
    }

    [Fact]
    public void DoubleQuotedFormatSpec_Works()
    {
        var e = new PrologEngine();
        e.Query("set_prolog_flag(double_quotes, codes).");
        Assert.Equal("a-b", Captured(e, "format(\"~w-~w\", [a,b])"));
    }

    [Fact]
    public void Format3_TakesAListSpecToo()
    {
        var e = new PrologEngine();
        Assert.Equal("hi",
            Captured(e, "current_output(S), format(S, [0'h,0'i], [])"));
    }

    [Fact]
    public void MixedCodesAndChars_IsATypeError()
    {
        // Not a third notation — the first element decides which list this is.
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("format([0'~, 'w'], [a])."));
        Assert.Contains("type_error", ex.Message);
    }

    [Fact]
    public void PartialListSpec_IsAnInstantiationError()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("format([0'a|_], [])."));
        Assert.Contains("instantiation_error", ex.Message);
    }
}

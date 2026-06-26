using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Structural equality (==/\==) over PSTR strings must terminate and be correct
/// (regression for the AreStrStructurallyEqual-on-Pstr infinite recursion).
/// </summary>
public class PstrEqualityTests
{
    private static PrologEngine StringEngine()
    {
        var e = new PrologEngine();
        e.Query("set_prolog_flag(double_quotes, string).");
        return e;
    }

    [Fact]
    public void EqualStringLiterals_AreEqual()
    {
        var e = StringEngine();
        Assert.True(e.Query("X = \"bar\", X == \"bar\".").Success);
    }

    [Fact]
    public void DifferentStringLiterals_AreNotEqual()
    {
        var e = StringEngine();
        Assert.False(e.Query("\"foo\" == \"bar\".").Success);
        Assert.True(e.Query("\"foo\" \\== \"bar\".").Success);
    }

    [Fact]
    public void StringFromDynamicFact_EqualsLiteral()
    {
        var e = StringEngine();
        e.ConsultString(":- dynamic s/1.\ns(\"foo\").\ns(\"bar\").\n");
        Assert.True(e.Query("s(X), X == \"bar\".").Success);
    }

    [Fact]
    public void EmptyStrings_AreEqual()
    {
        var e = StringEngine();
        Assert.True(e.Query("\"\" == \"\".").Success);
    }
}

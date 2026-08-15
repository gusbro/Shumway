using System;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>unicode_property/2</c> — the SWI library(unicode) subset the
/// Logtalk libraries rely on (json_path's iregexp <c>\p{...}</c> filters):
/// <c>category(C)</c> with the two-letter Unicode general category, exact
/// from the .NET tables.</summary>
public sealed class UnicodePropertyTests
{
    private static string CategoryOf(PrologEngine e, string codeExpr)
    {
        var sol = e.Query($"X is {codeExpr}, unicode_property(X, category(C)).");
        Assert.True(sol.Success);
        return ((AtomTerm)sol["C"]!).Name;
    }

    [Fact]
    public void GeneralCategories_AreExact()
    {
        var e = new PrologEngine();
        Assert.Equal("Lu", CategoryOf(e, "0'A"));
        Assert.Equal("Ll", CategoryOf(e, "0'a"));
        Assert.Equal("Nd", CategoryOf(e, "0'5"));
        Assert.Equal("Zs", CategoryOf(e, "0' "));
        Assert.Equal("Po", CategoryOf(e, "0'!"));
        Assert.Equal("Sm", CategoryOf(e, "0'+"));
        Assert.Equal("Cc", CategoryOf(e, "10"));
        // Beyond the BMP: a supplementary-plane letter (Deseret capital).
        Assert.Equal("Lu", CategoryOf(e, "0x10400"));
    }

    [Fact]
    public void BoundCategory_ActsAsATest()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("unicode_property(0'A, category('Lu')).").Success);
        Assert.False(e.Query("unicode_property(0'A, category('Nd')).").Success);
    }

    [Fact]
    public void UnboundCode_IsAnInstantiationError()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("unicode_property(_, category(_))."));
        Assert.Contains("instantiation_error", ex.Message);
    }

    [Fact]
    public void OutOfRangeCode_IsARepresentationError()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(
            () => e.Query("unicode_property(1200000, category(_))."));
        Assert.Contains("representation_error", ex.Message);
    }
}

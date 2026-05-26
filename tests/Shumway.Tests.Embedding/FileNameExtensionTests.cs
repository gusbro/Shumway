using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <c>file_name_extension/3</c> + <c>is_digit/1</c> — small
/// SWI / SICStus-compat builtins Blint.pl reaches for.
/// </summary>
public class FileNameExtensionTests
{
    [Fact]
    public void FileNameExtension_DecomposeFromFull()
    {
        var e = new PrologEngine();
        var sol = e.Query("file_name_extension(B, E, 'foo.pl').");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("foo"), sol["B"]);
        Assert.Equal(new AtomTerm("pl"), sol["E"]);
    }

    [Fact]
    public void FileNameExtension_DecomposeNoDot_EmptyExt()
    {
        var e = new PrologEngine();
        var sol = e.Query("file_name_extension(B, E, 'foo').");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("foo"), sol["B"]);
        Assert.Equal(new AtomTerm(""), sol["E"]);
    }

    [Fact]
    public void FileNameExtension_DecomposeMultipleDots_LastWins()
    {
        var e = new PrologEngine();
        var sol = e.Query("file_name_extension(B, E, 'a.b.c.txt').");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("a.b.c"), sol["B"]);
        Assert.Equal(new AtomTerm("txt"), sol["E"]);
    }

    [Fact]
    public void FileNameExtension_ComposeFromBaseAndExt()
    {
        var e = new PrologEngine();
        var sol = e.Query("file_name_extension(foo, pl, F).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("foo.pl"), sol["F"]);
    }

    [Fact]
    public void FileNameExtension_EmptyExt_OmitsDot()
    {
        var e = new PrologEngine();
        var sol = e.Query("file_name_extension(foo, '', F).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("foo"), sol["F"]);
    }

    [Fact]
    public void FileNameExtension_AllUnbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(file_name_extension(_, _, _), error(instantiation_error, _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void IsDigit_Digit_Succeeds()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("is_digit('0').").Success);
        Assert.True(e.Query("is_digit('5').").Success);
        Assert.True(e.Query("is_digit('9').").Success);
    }

    [Fact]
    public void IsDigit_NonDigit_Fails()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("is_digit('a').").Success);
        Assert.False(e.Query("is_digit('').").Success);
        Assert.False(e.Query("is_digit('12').").Success);
    }

    [Fact]
    public void IsDigit_Unbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(is_digit(_), error(instantiation_error, _), true).");
        Assert.True(sol.Success);
    }
}

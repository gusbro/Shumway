using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <see cref="PrologEngine.In"/> — where <c>user_input</c> reads from. The
/// counterpart of <see cref="PrologEngine.Out"/>, and the same contract: set it
/// before the first query, because the stream registry is built during query
/// setup and keeps whatever reader it was handed. Without it a host that has no
/// standard input (a browser) answers every <c>read/1</c> with
/// <c>end_of_file</c>, having asked nobody.
/// </summary>
public sealed class EngineInputTests
{
    private static PrologEngine WithInput(string text)
        => new() { In = new StringReader(text), Out = new StringWriter() };

    [Fact]
    public void ReadTakesATermFromTheSuppliedReader()
    {
        var e = WithInput("foo(bar).\n");
        var s = e.Query("read(X).");
        Assert.True(s.Success);
        Assert.Equal("foo(bar)", AstTermRenderer.Render(s["X"]!, 1200, e.Operators));
    }

    [Fact]
    public void SuccessiveReadsTakeSuccessiveTerms()
    {
        var e = WithInput("one. two. three.\n");
        var s = e.Query("read(A), read(B), read(C).");
        Assert.True(s.Success);
        Assert.Equal("one", Assert.IsType<AtomTerm>(s["A"]!).Name);
        Assert.Equal("two", Assert.IsType<AtomTerm>(s["B"]!).Name);
        Assert.Equal("three", Assert.IsType<AtomTerm>(s["C"]!).Name);
    }

    [Fact]
    public void ExhaustedInputIsEndOfFile()
    {
        var e = WithInput("only.\n");
        var s = e.Query("read(_), read(X).");
        Assert.True(s.Success);
        Assert.Equal("end_of_file", Assert.IsType<AtomTerm>(s["X"]!).Name);
    }

    [Fact]
    public void CharacterIoReadsFromItToo()
    {
        var e = WithInput("ab");
        var s = e.Query("get_char(A), get_char(B), get_char(C).");
        Assert.True(s.Success);
        Assert.Equal("a", Assert.IsType<AtomTerm>(s["A"]!).Name);
        Assert.Equal("b", Assert.IsType<AtomTerm>(s["B"]!).Name);
        Assert.Equal("end_of_file", Assert.IsType<AtomTerm>(s["C"]!).Name);
    }
}

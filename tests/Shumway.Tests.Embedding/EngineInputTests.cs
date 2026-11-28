using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <see cref="PrologEngine.In"/> — where <c>user_input</c> reads from. The
/// counterpart of <see cref="PrologEngine.Out"/>, and like it, set at any time:
/// a query before it must not leave <c>user_input</c> on the host's standard
/// input. Without it a host that has no standard input (a browser) answers
/// every <c>read/1</c> with <c>end_of_file</c>, having asked nobody.
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

    [Fact]
    public void InSetAfterAQuery_IsRead()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        Assert.True(e.Query("true.").Success);
        e.In = new StringReader("foo(bar).\n");
        var s = e.Query("read(X).");
        Assert.True(s.Success);
        Assert.Equal("foo(bar)", AstTermRenderer.Render(s["X"]!, 1200, e.Operators));
    }

    [Fact]
    public void InSetAgain_ReadsTheNewSource_OnTheSameStream()
    {
        var e = WithInput("first.\nleft_over.\n");
        Assert.True(e.Query("read(X), X == first.").Success);
        string before = e.Query("current_input(S).")["S"]!.ToString()!;

        e.In = new StringReader("second.\n");
        Assert.True(e.Query("read(X), X == second.").Success);
        Assert.Equal(before, e.Query("current_input(S).")["S"]!.ToString());
        Assert.True(e.Query("read(X), X == end_of_file.").Success);
    }

    [Fact]
    public void ARedirectedCurrentInput_StaysRedirected()
    {
        // set_input/1 moved the current input to another stream: a new host
        // reader changes what user_input reads, not that choice.
        var e = WithInput("");
        string file = Path.Combine(Path.GetTempPath(), "shumway-in-" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(file, "from_file.\n");
        try
        {
            string path = file.Replace('\\', '/');
            Assert.True(e.Query($"open('{path}', read, S), set_input(S), assertz(opened(S)).").Success);
            e.In = new StringReader("from_host.\n");
            Assert.True(e.Query("read(X), X == from_file.").Success);
            Assert.True(e.Query("read(user_input, X), X == from_host.").Success);
            Assert.True(e.Query("opened(S), close(S).").Success);
        }
        finally { File.Delete(file); }
    }
}

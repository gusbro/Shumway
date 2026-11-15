using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>PrologEngine.Error: what user_error writes to. Each engine has
/// its own, so a host with several keeps their diagnostics apart.</summary>
public sealed class EngineErrorWriterTests
{
    [Fact]
    public void UserError_WritesToTheEnginesOwnWriter()
    {
        var one = new PrologEngine { Error = new StringWriter(), Out = new StringWriter() };
        var two = new PrologEngine { Error = new StringWriter(), Out = new StringWriter() };

        Assert.True(one.Query(@"write(user_error, from_one), nl(user_error).").Success);
        Assert.True(two.Query(@"write(user_error, from_two), nl(user_error).").Success);

        Assert.Contains("from_one", one.Error.ToString());
        Assert.DoesNotContain("from_two", one.Error.ToString());
        Assert.Contains("from_two", two.Error.ToString());
        Assert.DoesNotContain("from_one", two.Error.ToString());
        // user_error is not user_output.
        Assert.Equal("", one.Out.ToString());
    }

    [Fact]
    public void PrintMessage_GoesThere()
    {
        var e = new PrologEngine { Error = new StringWriter() };

        Assert.True(e.Query(@"print_message(warning, careful).").Success);

        Assert.Contains("careful", e.Error.ToString());
    }

    [Fact]
    public void AWriterGivenAfterTheFirstQuery_IsTheOneWrittenTo()
    {
        var before = new StringWriter();
        var after = new StringWriter();
        var e = new PrologEngine { Error = before };
        Assert.True(e.Query(@"write(user_error, first).").Success);

        e.Error = after;
        Assert.True(e.Query(@"write(user_error, second).").Success);

        Assert.Equal("first", before.ToString());
        Assert.Equal("second", after.ToString());
    }

    [Fact]
    public void AStreamTakingTheAlias_StillTakesIt()
    {
        var error = new StringWriter();
        var e = new PrologEngine { Error = error };
        string path = Path.Combine(Path.GetTempPath(), "shumway-err-" + System.Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert.True(e.Query(
                $@"open('{path.Replace('\\', '/')}', write, S), set_stream(S, alias(user_error)),
                   write(user_error, to_the_file), close(S).").Success);

            Assert.Equal("to_the_file", File.ReadAllText(path));
            Assert.Equal("", error.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

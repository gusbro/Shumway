using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A diagnostic about text an <c>:- include/1</c> pulled in names the
/// included file. It named the file being compiled, at the included file's
/// line, and a syntax error there read
/// <c>root.pl:0:0: error: 2:9: in included file '...': 2:9: ...</c>.</summary>
public sealed class IncludeDiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_incdiag_" + Guid.NewGuid().ToString("N"));

    public IncludeDiagnosticsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void AWarningInAnIncludedFileNamesThatFile()
    {
        string root = Write("root.pl", ":- include(sub).\nmain.\n:- initialization(main).\n");
        Write("sub.pl", "p(1).\n:- initialization(p(_)).\n");

        var res = ShmoCompiler.TryCompileFile(root, ShmoBuildMode.Release);

        Assert.True(res.Success);
        Assert.Collection(res.Warnings,
            w =>
            {
                Assert.Equal(Path.Combine(_dir, "sub.pl"), w.File, ignoreCase: true);
                Assert.Equal(2, w.Line);
            },
            w =>
            {
                Assert.Null(w.File);
                Assert.Equal(3, w.Line);
                Assert.Equal("root.pl:3:1", w.Where("root.pl"));
            });
    }

    [Fact]
    public void ASyntaxErrorInAnIncludedFileNamesThatFile()
    {
        string root = Write("root.pl", ":- include(bad).\n");
        Write("bad.pl", "p(1).\np(2) :- .\n");

        var res = ShmoCompiler.TryCompileFile(root, ShmoBuildMode.Release);

        var err = Assert.Single(res.Errors);
        Assert.Equal(Path.Combine(_dir, "bad.pl"), err.File, ignoreCase: true);
        Assert.Equal((2, 9), (err.Line, err.Column));
        Assert.DoesNotContain("included file", err.Message);
        Assert.DoesNotContain("2:9", err.Message);
    }

    [Fact]
    public void AConsultNamesTheIncludedFileOnce()
    {
        string root = Write("root.pl", ":- include(bad).\n");
        Write("bad.pl", "p(1).\np(2) :- .\n");

        var e = new PrologEngine { Out = new StringWriter() };
        var ex = Assert.ThrowsAny<Exception>(() => e.ConsultFile(root));

        string bad = Path.Combine(_dir, "bad.pl");
        Assert.Contains($"2:9: in included file '{bad}': Unexpected", ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ex.Message, "2:9"));
    }
}

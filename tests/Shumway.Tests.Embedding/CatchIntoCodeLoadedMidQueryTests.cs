using System.IO;
using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A query that loads a program and then runs it: the load moves the code to a
/// larger array, and a catch/3 of the loaded code resumes into its recovery,
/// which the load added. Logtalk does this in one goal: it loads lgtunit and a
/// test file, then runs tests that expect errors.
/// </summary>
public sealed class CatchIntoCodeLoadedMidQueryTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "shumway-midquery-catch-" + System.Guid.NewGuid().ToString("N"));

    public CatchIntoCodeLoadedMidQueryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Enough clauses that loading them outgrows the code array a fresh
    // engine starts with.
    private string Program(string catcher)
    {
        var text = new StringBuilder();
        for (int k = 0; k < 20; k++)
            for (int i = 0; i < 400; i++)
                text.Append($"fact{k}({i}, a{i}, [x{i}, y, z]).\n");
        text.Append(catcher);
        string path = Path.Combine(_dir, "loaded.pl").Replace('\\', '/');
        File.WriteAllText(path, text.ToString());
        return path;
    }

    [Theory]
    [InlineData(@"guarded(R) :- catch((X = foo, Y is X + 1, Y > 0), error(E, _), R = caught(E)).",
        "caught(type_error(evaluable,foo/0))")]
    [InlineData(@"guarded(R) :- catch(atom_length(_, _), error(E, _), R = caught(E)).",
        "caught(instantiation_error)")]
    [InlineData(@"guarded(R) :- catch(throw(ball), B, R = caught(B)).",
        "caught(ball)")]
    public void ACatchOfCodeLoadedInTheSameQuery_ResumesInIt(string catcher, string expected)
    {
        string path = Program(catcher + "\n");
        var e = new PrologEngine();

        // The whole program loaded, the last clause included.
        var answer = e.Query($"consult('{path}'), guarded(R), R == {expected}, fact19(399, a399, _).");

        Assert.True(answer.Success);
    }
}

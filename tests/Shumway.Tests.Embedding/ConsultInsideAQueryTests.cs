using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A query that consults sees, for the rest of it, the program the
/// next query will see. consult/1 appends and reconsult/1 replaces what the
/// file defines; inside a query, consult/1 showed only the new file's clauses
/// of a predicate it extended, and reconsult/1 none of them.</summary>
public sealed class ConsultInsideAQueryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-inquery-{Guid.NewGuid():N}");

    public ConsultInsideAQueryTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.pl"), "p(1).\n");
        File.WriteAllText(Path.Combine(_dir, "b.pl"), "p(2).\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathOf(string name) => Path.Combine(_dir, name).Replace(Path.DirectorySeparatorChar, '/');

    [Theory]
    [InlineData("consult", "consult", "[1,2]")]
    [InlineData("consult", "consult(a)", "[1,1]")]
    [InlineData("consult", "reconsult", "[2]")]
    [InlineData("reconsult", "reconsult", "[2]")]
    public void TheQuerySeesWhatTheNextOneSees(string first, string second, string expected)
    {
        string secondGoal = second == "consult(a)"
            ? $"consult('{PathOf("a.pl")}')"
            : $"{second}('{PathOf("b.pl")}')";
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"{first}('{PathOf("a.pl")}'), {secondGoal}, findall(X, p(X), L), L == {expected}.").Success,
            "inside the query");
        Assert.True(e.Query($"findall(X, p(X), L), L == {expected}.").Success, "the next query");
    }
}

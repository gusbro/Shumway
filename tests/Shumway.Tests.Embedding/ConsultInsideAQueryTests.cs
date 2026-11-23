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

    /// <summary>A load the consult refuses whole throws to the host that
    /// asked for it. Asked by a goal, it is an error the program catches: the
    /// host's exception ended the query, whatever the query had caught.</summary>
    [Theory]
    [InlineData("consult")]
    [InlineData("reconsult")]
    [InlineData("ensure_loaded")]
    public void ALoadTheConsultRefuses_IsAnErrorTheQueryCatches(string load)
    {
        File.WriteAllText(Path.Combine(_dir, "apart.pl"), "r(1).\ns(1).\nr(2).\n");

        Assert.Throws<InvalidOperationException>(() => new PrologEngine().ConsultFile(PathOf("apart.pl")));
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"catch({load}('{PathOf("apart.pl")}'), error(system_error(Why), _), true), "
            + "atom(Why), sub_atom(Why, _, _, _, 'r/1 are not contiguous'), "
            + $"consult('{PathOf("a.pl")}'), p(1).").Success);
        Assert.True(e.Query("p(1).").Success, "the next query");
    }

    /// <summary>A load asked by a directive of a file that is loading is that
    /// file's: the directive is reported and the file goes on, whoever asked
    /// for the file.</summary>
    [Fact]
    public void ALoadRefusedInsideAFileThatLoads_IsThatFilesDirectiveThatFailed()
    {
        File.WriteAllText(Path.Combine(_dir, "apart.pl"), "r(1).\ns(1).\nr(2).\n");
        File.WriteAllText(Path.Combine(_dir, "outer.pl"), $":- consult('{PathOf("apart.pl")}').\nt(1).\n");
        var warnings = new StringWriter();
        var byHost = new PrologEngine { Warnings = warnings };

        byHost.ConsultFile(PathOf("outer.pl"));

        Assert.Contains("r/1 are not contiguous", warnings.ToString());
        Assert.True(byHost.Query("t(1).").Success);
        Assert.True(new PrologEngine().Query($"consult('{PathOf("outer.pl")}'), t(1).").Success);
    }
}

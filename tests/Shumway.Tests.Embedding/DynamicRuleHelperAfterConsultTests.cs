using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A dynamic predicate's rule calls a predicate a later file defines.
/// A query runs between the two consults (a file's initialization goal), so
/// the rule's control-construct helper is first compiled while the callee is
/// undefined. The helper keeps its name across setups (its transform is
/// memoized), and its compiled code must follow the program: once the callee
/// is consulted, the call reaches it. Logtalk's paths file has this shape
/// (<c>logtalk_library_path/2</c>'s packs rule calls the core's alias
/// expansion, and its initialization asserts into the table), and its
/// startup did not get past the first alias.</summary>
public sealed class DynamicRuleHelperAfterConsultTests : IDisposable
{
    private const string Table = """
        :- multifile(lp/2).
        :- dynamic(lp/2).
        cwd('/cwd').
        lp(home, '/h/').
        lp(Pack, PackPath) :-
            (   expand_alias(packs, P) -> atom_concat(P, '/packs', Base)
            ;   Base = '/none'
            ),
            atom_concat(Base, '/', Base1),
            atom_concat(Base1, Pack, PackPath).
        """;

    // The query between the consults: one that leaves the table alone, and
    // one that asserts into it (the table's rewrite cache entry goes with
    // the mutation, so the next setup derives it afresh).
    private const string QueryOnly = ":- initialization(( cwd(_) -> true ; true )).\n";
    private const string Asserting = ":- initialization(( cwd(D) -> asserta(lp(startup, D)) ; true )).\n";

    private const string Second = """
        expand_alias(packs, '/root/packs').
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shumway_drh_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Load(PrologEngine e, bool files, string source, string name)
    {
        if (!files) { e.ConsultString(source); return; }
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        e.ConsultFile(path);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void TheRulesHelper_ReachesACalleeConsultedAfterItsFirstCompile(bool tier, bool files, bool asserting)
    {
        var e = new PrologEngine();
        if (!tier) e.IlPromotion.Threshold = 0;
        Load(e, files, Table + "\n" + (asserting ? Asserting : QueryOnly), "first.pl");
        Load(e, files, Second, "second.pl");
        Assert.True(e.Query("lp(foo, X), X == '/root/packs/packs/foo'.").Success);
        // And again, from a second setup.
        Assert.True(e.Query("lp(bar, X), X == '/root/packs/packs/bar'.").Success);
        if (asserting) Assert.True(e.Query("lp(startup, D), D == '/cwd'.").Success);
    }

    /// <summary>The top level's way: default library directories, a session
    /// (answers elided for display), files consulted, the query enumerated.</summary>
    [Fact]
    public void TheTopLevelsWay_ReachesTheCallee()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.AddDefaultLibraryDirectories();
        var session = new Shumway.TopLevel.TopLevelSession(e);
        Assert.NotNull(session);
        Load(e, files: true, Table + "\n" + Asserting, "first.pl");
        Load(e, files: true, Second, "second.pl");
        using var answers = e.QueryAll("lp(foo, X).").GetEnumerator();
        Assert.True(answers.MoveNext());
        Assert.Equal("/root/packs/packs/foo", answers.Current["X"]!.ToString());
    }

    [Fact]
    public void BeforeTheCallee_TheElseBranchRuns()
    {
        // Anti-vacuity: the shape itself behaves without the second file.
        var e = new PrologEngine();
        e.ConsultString(Table + "\n" + QueryOnly);
        Assert.True(e.Query("catch(lp(foo, X), error(existence_error(procedure, expand_alias/2), _), X = raised), X == raised.").Success);
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The <c>version_data</c> Prolog flag: <c>shumway(Major, Minor, Patch, [])</c>,
/// the GProlog/SWI convention. The Logtalk adapter derives its
/// <c>prolog_version</c> backend feature from it at runtime.
/// </summary>
public class VersionFlagTests
{
    [Fact]
    public void VersionData_ReportsEngineVersion()
    {
        var engine = new PrologEngine();
        var solution = engine.Query(
            "current_prolog_flag(version_data, shumway(Ma, Mi, Pa, Extra)).");
        Assert.True(solution.Success);
        Assert.Equal(PrologEngine.VersionMajor, solution.Get<int>("Ma"));
        Assert.Equal(PrologEngine.VersionMinor, solution.Get<int>("Mi"));
        Assert.Equal(PrologEngine.VersionPatch, solution.Get<int>("Pa"));
        Assert.Equal("[]", solution["Extra"]!.ToString());
    }

    [Fact]
    public void VersionData_IsEnumerated()
    {
        // With Flag unbound, version_data must appear in the ISO §8.17.2
        // enumeration alongside the other readable flags.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(F, current_prolog_flag(F, _), Fs), memberchk(version_data, Fs).").Success);
    }

    [Fact]
    public void VersionData_WrongFunctorFails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query(
            "current_prolog_flag(version_data, gprolog(_, _, _, _)).").Success);
    }
}

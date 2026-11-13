using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The <c>version_data</c> Prolog flag: <c>shumway(Major, Minor, Patch, Build)</c>,
/// the GProlog/SWI convention, <c>Build</c> a list of <c>Name-Value</c> pairs
/// (<c>git</c>, <c>runtime</c>, <c>arch</c>). The Logtalk adapter derives its
/// <c>prolog_version</c> backend feature from it at runtime.
/// </summary>
public class VersionFlagTests
{
    [Fact]
    public void VersionData_ReportsEngineVersion()
    {
        var engine = new PrologEngine();
        var solution = engine.Query(
            "current_prolog_flag(version_data, shumway(Ma, Mi, Pa, _)).");
        Assert.True(solution.Success);
        Assert.Equal(PrologEngine.VersionMajor, solution.Get<int>("Ma"));
        Assert.Equal(PrologEngine.VersionMinor, solution.Get<int>("Mi"));
        Assert.Equal(PrologEngine.VersionPatch, solution.Get<int>("Pa"));
    }

    [Fact]
    public void VersionData_ListsTheBuildAsNameValuePairsOfAtoms()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "current_prolog_flag(version_data, shumway(_, _, _, B)), is_list(B), "
            + "forall(member(P, B), (P = N-V, atom(N), atom(V))).").Success);
        string runtime = PrologEngine.RuntimeVersion(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        string arch = PrologEngine.ArchName(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        Assert.True(engine.Query(
            $"current_prolog_flag(version_data, shumway(_, _, _, B)), memberchk(runtime-'{runtime}', B), memberchk(arch-'{arch}', B).").Success);
        // A build in a git checkout carries its commit; this one is built from the repository.
        string? hash = PrologEngine.BuildInfo.FirstOrDefault(p => p.Name == "git").Value;
        Assert.False(string.IsNullOrEmpty(hash), "no commit in " + PrologEngine.BuildInfo.Count + " build entries");
        Assert.True(engine.Query(
            $"current_prolog_flag(version_data, shumway(_, _, _, B)), memberchk(git-'{hash}', B).").Success);
    }

    [Theory]
    [InlineData("0.9.3+5f68294431b84c63d28dcd07e69fbbdcfaa90bc0", "5f68294")]
    [InlineData("0.9.3+ABCDEF0123", "abcdef0")]
    [InlineData("0.9.3+1234567", "1234567")]
    [InlineData("0.9.3", null)]
    [InlineData("0.9.3+123", null)]
    [InlineData("0.9.3+12345zz", null)]
    [InlineData(null, null)]
    public void GitShortHash_TakesSevenHexDigitsAfterThePlus(string? stamp, string? expected)
        => Assert.Equal(expected, PrologEngine.GitShortHash(stamp));

    [Theory]
    [InlineData(".NET 10.0.15", "10.0.15")]
    [InlineData(".NET Framework 4.8.9221.0", "4.8.9221.0")]
    [InlineData(".NET 11.0.0-rc.2.25502.107", "11.0.0-rc.2.25502.107")]
    public void RuntimeVersion_IsTheDescriptionsLastWord(string description, string expected)
        => Assert.Equal(expected, PrologEngine.RuntimeVersion(description));

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "x64")]
    [InlineData(System.Runtime.InteropServices.Architecture.X86, "x86")]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, "arm64")]
#if !NETFRAMEWORK
    [InlineData(System.Runtime.InteropServices.Architecture.Wasm, "wasm")]   // WebShumway
#endif
    public void ArchName_IsTheArchitectureInLowerCase(System.Runtime.InteropServices.Architecture architecture, string expected)
        => Assert.Equal(expected, PrologEngine.ArchName(architecture));

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

using System.Reflection;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Everything that states Shumway's version must state the SAME
/// version: the <c>version_data</c> Prolog flag, the top-level banner, and the
/// assembly stamp (<c>&lt;Version&gt;</c> in Directory.Build.props). The
/// constants on <see cref="PrologEngine"/> are the single source; this pins
/// the others to them so a bump cannot land half-applied.</summary>
public sealed class VersionAgreementTests
{
    [Fact]
    public void Banner_CarriesTheVersionAndTheProcessBitness()
    {
        string banner = PrologEngine.VersionBanner;
        Assert.StartsWith("Shumway Prolog " + PrologEngine.VersionString, banner);
        Assert.EndsWith(
            System.Environment.Is64BitProcess ? "(64 bits)" : "(32 bits)", banner);
    }

    [Fact]
    public void Banner_AndVersionDataFlag_AgreeDigitForDigit()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "current_prolog_flag(version_data, shumway(Ma, Mi, Pa, _)), "
            + "number_codes(Ma, MaC), number_codes(Mi, MiC), number_codes(Pa, PaC), "
            + "atom_codes(MaA, MaC), atom_codes(MiA, MiC), atom_codes(PaA, PaC), "
            + "atomic_list_concat([MaA, '.', MiA, '.', PaA], V).");
        Assert.True(sol.Success);
        string fromFlag = sol.Get<string>("V");
        Assert.Equal(PrologEngine.VersionString, fromFlag);
        Assert.Contains(fromFlag, PrologEngine.VersionBanner);
    }

    [Fact]
    public void AssemblyStamp_MatchesTheEngineConstants()
    {
        // Directory.Build.props' <Version> feeds InformationalVersion; a bump
        // in one place and not the other is exactly what this catches.
        var asm = typeof(PrologEngine).Assembly;
        string? info = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        Assert.NotNull(info);
        // The SDK may append source-revision metadata (`0.9.0+abc123`).
        string stamped = info!.Split('+')[0];
        Assert.Equal(PrologEngine.VersionString, stamped);
    }
}

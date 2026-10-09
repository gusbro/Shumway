using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The engine's libraries ship their Tier-1 IL, baked for the
/// framework of the build: the .NET Framework flavour's references mscorlib,
/// which a .NET Framework host binds, where the net10 flavour's would not.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class EngineLibraryCompiledCodeTests : IDisposable
{
    private readonly int _savedThreshold = IlPromotionStore.DefaultPersistedThreshold;

    // Bound at load, so that binding is what the test sees.
    public EngineLibraryCompiledCodeTests() => IlPromotionStore.DefaultPersistedThreshold = 0;

    public void Dispose() => IlPromotionStore.DefaultPersistedThreshold = _savedThreshold;

    private static int Fid(string n, int a) => FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    [Fact]
    public void AnEngineLibrary_RunsItsBakedCode()
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Out = new StringWriter(), Warnings = warnings };
        e.UseClpfd();
        e.UseCoroutining();
        Assert.True(e.IlPromotion.IsPromoted(Fid("#=", 2)), "#=/2 has no compiled code");
        Assert.True(e.IlPromotion.IsPromoted(Fid("dif", 2)), "dif/2 has no compiled code");
        Assert.Equal("", warnings.ToString());
        Assert.True(e.Query("X #= 3 + 4, X == 7, dif(Y, a), Y = b.").Success);
    }
}

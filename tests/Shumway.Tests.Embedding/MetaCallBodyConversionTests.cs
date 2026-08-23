using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>
/// SS7.6.2 body conversion at a runtime call/N boundary (MetaBodyConvert):
/// a variable in goal position inside a metacalled body converts to
/// <c>call(V)</c> UP FRONT, so a <c>!</c> the variable is later bound to
/// cuts only within its own metacall. The conversion must not re-run at the
/// <c>'$call'/2</c> sub-dispatches — by then the variable's home cell holds
/// a plain <c>!</c>, indistinguishable from a literal one.
/// </summary>
public class MetaCallBodyConversionTests
{
    private const string Query =
        "G = (C=(!), (X=1,C;X=2)), findall(X, call(G), L), L == [1, 2].";

    [Fact]
    public void AVariableBoundToCutMidBodyKeepsItsOwnBarrier()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(Query).Success);
    }

    [Fact]
    public void ALiteralCutInAMetacalledBodyStillCutsTheCall()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "G = (X=1,! ;X=2), findall(X, call(G), L), L == [1].").Success);
    }

    [Fact]
    public void TheModuleTagIsTransparentToTheConversion()
    {
        // PrepareMqualGoal distributes '$mqual' over a control construct's
        // args before the boundary converts, so the walk must see through it.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "G = (C=(!), (X=1,C;X=2)), findall(X, call(user:G), L), L == [1, 2].").Success);
    }

    [Fact]
    public void TheTier1TwinConvertsToo()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(
            "t(L) :- G = (C=(!), (X=1,C;X=2)), findall(X, call(G), L).");
        for (int i = 0; i < 3; i++)
            Assert.True(e.Query("t(L), L == [1, 2].").Success);
        Assert.True(e.IlPromotion.PromotedCount > 0,
            "t/1 did not promote - this test is not exercising Tier-1");
        Assert.True(e.Query("t(L), L == [1, 2].").Success);
    }

    [Theory]
    [InlineData("call(X)")]
    [InlineData("once(X)")]
    [InlineData("findall(_, X, _)")]
    [InlineData("\\+ X")]
    public void ANumberInARuntimeBodyRaisesBeforeAnythingRuns(string wrap)
    {
        // GNU raises type_error(callable, (fail,3)) for all four, with the
        // WHOLE construct as culprit and `fail` never executing.
        var e = new PrologEngine();
        var r = e.Query(
            $"X = (fail, 3), catch({wrap}, error(type_error(callable, C), _), true), C == (fail, 3).");
        Assert.True(r.Success);
    }

    [Fact]
    public void TheTier1TwinChecksNumbersToo()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(
            "t :- X = (fail, 3), catch(call(X), error(type_error(callable, C), _), true), C == (fail, 3).");
        for (int i = 0; i < 3; i++)
            Assert.True(e.Query("t.").Success);
        Assert.True(e.IlPromotion.PromotedCount > 0,
            "t/0 did not promote - this test is not exercising Tier-1");
        Assert.True(e.Query("t.").Success);
    }
}

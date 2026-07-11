using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// Chunk 120 (Phase 8, ADR-015 chunk C, bytecode-level dispatch):
/// foundation for generation-filtered dynamic dispatch.
///
/// <para>Adds the <c>ViewGen</c> slot to the choice-point frame and has
/// <c>PushChoicePoint</c> / <c>RestoreCommonFromCurrentCp</c> save and
/// restore it alongside the rest of engine state. Nothing yet samples
/// <see cref="Activation.CurrentViewGen"/> (the upcoming <c>EnterDynamic</c>
/// opcode will), so the field stays at 0 in practice and no existing
/// behaviour shifts. These tests pin the save/restore contract so the
/// upcoming <c>CheckVisible</c> opcode can rely on it.</para>
/// </summary>
public class Chunk120Tests
{
    [Fact]
    public void PushChoicePoint_CapturesCurrentViewGen()
    {
        var engine = new Activation();
        engine.CurrentViewGen = 42;
        engine.PushChoicePoint(arity: 0, nextClauseAddr: 0);

        Assert.Equal(42L, engine.ViewGenOf(engine.B, arity: 0));
    }

    [Fact]
    public void RetryMeElse_RestoresViewGenFromCp()
    {
        var engine = new Activation();
        engine.CurrentViewGen = 42;
        engine.PushChoicePoint(arity: 0, nextClauseAddr: 0);

        // Whatever happens during the first clause, when we retry, the
        // view-gen the call started with must be back in place.
        engine.CurrentViewGen = 99;
        engine.RetryMeElse(nextClauseAddr: 1);
        Assert.Equal(42L, engine.CurrentViewGen);
    }

    [Fact]
    public void TrustMe_RestoresViewGenAlongsideTheRestOfState()
    {
        var engine = new Activation();
        engine.CurrentViewGen = 7;
        engine.PushChoicePoint(arity: 0, nextClauseAddr: 0);

        engine.CurrentViewGen = 13;
        engine.TrustMe();
        Assert.Equal(7L, engine.CurrentViewGen);
    }

    [Fact]
    public void NestedChoicePoints_EachCarriesItsOwnViewGen()
    {
        var engine = new Activation();
        engine.CurrentViewGen = 1;
        engine.PushChoicePoint(arity: 0, nextClauseAddr: 0);

        engine.CurrentViewGen = 2;
        engine.PushChoicePoint(arity: 0, nextClauseAddr: 0);

        engine.CurrentViewGen = 999;
        engine.TrustMe();       // pops inner CP; restores 2
        Assert.Equal(2L, engine.CurrentViewGen);

        engine.CurrentViewGen = 998;
        engine.TrustMe();       // pops outer CP; restores 1
        Assert.Equal(1L, engine.CurrentViewGen);
    }
}

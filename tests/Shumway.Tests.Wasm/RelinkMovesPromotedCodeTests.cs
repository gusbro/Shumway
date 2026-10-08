using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A consult can move code the tier has already compiled: a helper
/// that disappears leaves a hole the next link closes, and everything above
/// it slides down. The modules keep their build addresses, and the tier
/// translates them to the live ones at every exit to the interpreter -- which
/// it can only do once it has been told where the code went.
///
/// <para>It was told by a host tick, and a lazy-mode host never ticked: the
/// browser's selftest promoted clpfd, consulted one more file, and the next
/// constraint deopted into the middle of another predicate ("reserved_invalid
/// opcode ... bytecode corruption"). The query setup that builds the new link
/// now reconciles the tier itself, before the query can run.</para></summary>
public sealed class RelinkMovesPromotedCodeTests
{
    [Fact]
    public void AConsultThatMovesPromotedCode_LeavesItRunnable()
    {
        // The findall leaves a helper the next link drops, so clpfd, loaded
        // after it and promoted by the first constraint, slides down.
        var (e, members, _) = TieredEngine.BuildWithWorld(
            "d(L) :- findall(Y, q(Y), L).\nq(1).\n", wasmThreshold: 1);
        e.ConsultString(":- use_module(library(clpfd)).\n");
        Assert.True(e.Query("X #> 3, X #< 7.").Success);
        Assert.NotEmpty(members);                       // clpfd was promoted
        int deadBefore = e.StaticDeadRegions.Count;

        e.ConsultString("r(1).\n");
        Assert.True(e.Query("true.").Success);
        Assert.True(e.StaticDeadRegions.Count > deadBefore,
            "the consult no longer moves code, so this test proves nothing");

        Assert.True(e.Query(
            "L = [A,B,C], L ins 1..3, all_distinct(L), A #< B, B #< C, label(L),"
            + " L == [1,2,3].").Success);
    }
}

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 16+ chunk 186: <c>Engine.UnifyVariableX</c> /
/// <c>PutStructure</c> / <c>PutList</c> auto-grow the X-register bank
/// when the IL emit passes a slot beyond the current capacity. The
/// bytecode interpreter's opcode handlers route through
/// <see cref="Engine.SetRegister"/> which already grows; the IL
/// emit's direct method calls used to crash with
/// <c>IndexOutOfRangeException</c> for predicates whose temp-var
/// slots exceeded the default register count (256). Surfaced
/// linting Blint with <c>SHUMWAY_IL_PROMOTE=1</c>.
/// </summary>
public class Chunk186Tests
{
    [Fact]
    public void Tier1WithDeepStructureUnification_GrowsRegisters()
    {
        // A clause with > 200 temp variables forces the WAM compiler
        // to emit unify_variable_x with high slot numbers. Under
        // promote=1, that clause is IL'd on first call; the IL emit
        // calls Engine.UnifyVariableX(slot) which must grow the X
        // register bank.
        // Build a 300-arg compound by hand.
        var args = string.Join(",", System.Linq.Enumerable.Range(0, 300).Select(i => $"V{i}"));
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            $":- public big/300.\n"
            + $"big({args}) :- true.\n");
        var sol = engine.Query($"big({args}).");
        Assert.True(sol.Success);
    }
}

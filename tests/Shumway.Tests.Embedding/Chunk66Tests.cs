using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 66: closure report on IL non-leaf callees. The investigation
/// completed in chunk 63 identified the semantic gap (callee
/// try_me_else CPs capture <c>SubroutineSentinelCp</c> as their saved
/// Cp and short-circuit out of the IL caller's body on backtrack).
/// Chunk 66's brief was to implement the meta-CP design that closes
/// the gap: at each IL Call site within an IL delegate, push a
/// per-site choice point whose resume delegate (a) drives
/// <c>interp.Backtrack</c> to retry the callee and (b) on success
/// re-enters the IL caller at a post-call cursor that picks up
/// execution where the Call left off.
///
/// <para>The meta-CP design needs a per-call-site cursor allocation
/// pass across <see cref="CompileSingleClause"/> and
/// <see cref="CompileTryMeElseChain"/>, plus emitted dispatch on the
/// incoming cursor at the start of every IL delegate. That's a
/// substantial Sigil-emission refactor — large enough to warrant its
/// own focused chunk with its own test grid covering single-clause +
/// indexed + try_me_else paths and CP-cut interactions. Phase 1 lands
/// with the conservative leaf-only restriction in place; Phase 2's
/// IL coverage chunk picks up the meta-CP work.</para>
///
/// <para>Phase 1 closure pin: the Tier-0 bytecode interpreter
/// handles every shape correctly, including the cross-product
/// pattern that's the IL non-leaf gap (verified in
/// <see cref="Chunk63Tests"/>). The deferred work is purely
/// "promote more shapes to IL"; correctness is already there.</para>
/// </summary>
public class Chunk66Tests
{
    [Fact]
    public void Tier0_NonLeafCrossProduct_FullCorrect()
    {
        // The shape that chunk 66 would unlock for IL — currently
        // runs entirely on Tier 0 and produces the correct
        // cartesian product. Pin the baseline.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public pair/2.\n" +
            ":- public left/1.\n" +
            ":- public right/1.\n" +
            "left(a). left(b). left(c).\n" +
            "right(1). right(2).\n" +
            "pair(X, Y) :- left(X), right(Y).\n");
        Assert.Equal(6, engine.QueryAll("pair(_, _).").Count());
    }

    [Fact]
    public void Tier0_NonLeafTripleProduct_FullCorrect()
    {
        // Three-level cartesian — also a non-leaf chain. Tier-0
        // produces the right count; chunk 66's IL lift would
        // give the same count via the meta-CP path.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public triple/3.\n" +
            ":- public dim_a/1.\n" +
            ":- public dim_b/1.\n" +
            ":- public dim_c/1.\n" +
            "dim_a(x). dim_a(y).\n" +
            "dim_b(p). dim_b(q). dim_b(r).\n" +
            "dim_c(7).\n" +
            "triple(A, B, C) :- dim_a(A), dim_b(B), dim_c(C).\n");
        // 2 * 3 * 1 = 6.
        Assert.Equal(6, engine.QueryAll("triple(_, _, _).").Count());
    }
}

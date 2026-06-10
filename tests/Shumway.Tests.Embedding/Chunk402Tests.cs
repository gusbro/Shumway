using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 402 (Phase 29) — regression guard for a PRE-EXISTING <c>--strip-wam</c> bug
/// (independent of region compilation, present since the chunk-27x strip):
/// <c>catch/3</c>'s recovery path resolves the recovery predicate through
/// <c>CurrentFunctorAddresses</c>, and a stripped predicate's "address" there is its
/// resume-marker alias (<c>EncodeResumeMarker(fid, 0)</c>, far above the code range).
/// <c>RunCatching</c> then called <c>interp.Run(program, marker)</c> whose entry bounds
/// guard threw <c>ArgumentOutOfRangeException: startPc 0x40… is outside [0, …)</c>
/// BEFORE the dispatch loop's <c>IsResumeMarker</c> routing could see it. Observed on
/// Blint built as <c>--exe --strip-wam</c> when given a non-existent file: the
/// file-open existence_error unwinds into <c>main/0</c>'s catch, whose recovery lives
/// in a stripped predicate. Fix: <c>Run</c> admits a resume-marker start PC (the loop
/// routes it to the IL delegate, the same path a meta-call to a stripped predicate
/// already takes).
/// </summary>
public class Chunk402Tests
{
    // handler/2 is a plain single-clause predicate → standalone IL → its WAM is
    // stripped under --strip-wam. catch/3 names it as the recovery goal, so the
    // throw → TryCatch → Run(recovery address) path runs it BY ADDRESS — the address
    // being the resume-marker alias once the WAM is gone.
    private const string Source =
        ":- public go/2.\n"
        + "handler(E, caught(E)).\n"
        + "risky(1, ok).\n"
        + "risky(_, _) :- throw(boom).\n"
        + "go(N, R) :- catch(risky(N, R), E, handler(E, R)).\n";

    private static PrologEngine Build(bool stripWam)
    {
        var obj = ShmoCompiler.CompileSource(Source, "user");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("go", 2) },
            IncludeCompiledIl = true,
            StripWam = stripWam,
        });
        Assert.True(result.Success);
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        return engine;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // the regression: recovery predicate's WAM stripped
    public void CatchRecovery_IntoStrippedPredicate_Runs(bool stripWam)
    {
        var engine = Build(stripWam);
        // Non-throwing path first (sanity).
        Assert.True(engine.Query("go(1, R), R == ok.").Success);
        // The throwing path: recovery handler/2 must run — by resume-marker
        // address when stripped (was: ArgumentOutOfRangeException startPc).
        Assert.True(engine.Query("go(2, R), R == caught(boom).").Success);
    }
}

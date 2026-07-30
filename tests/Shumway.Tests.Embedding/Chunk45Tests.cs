using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 45: the compiled bytecode payload that bundles can carry
/// (chunk 38) is now used at load time — <see cref="PrologEngine.LoadBundle(Bundle)"/>
/// decodes the payload, walks its predicates, and pre-warms the
/// Tier-1 IL promotion store for everyone the IL compiler can handle.
/// The first call into an eligible predicate hits IL instead of
/// waiting for the invocation counter to cross
/// <see cref="IlPromotionStore.Threshold"/>.
///
/// <para>True AOT — emitting a .dll via <c>PersistedAssemblyBuilder</c>
/// and skipping IL emission entirely at load time — needs portable IL
/// emission (atom / functor / builtin ids cross-process), so it lands
/// in a future chunk with its own ADR. This chunk delivers the
/// build-time-compile-once half of that story: ship the bytecode in
/// the bundle so loaders skip the per-startup WAM compile.</para>
/// </summary>
public class Chunk45Tests
{
    private static int FunctorId(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // ============================================================================
    // LoadBundle pre-warm path
    // ============================================================================

    [Fact]
    public void LoadBundle_WithBlob_PreWarmsIl()
    {
        // Build a bundle that includes the compiled blob, then load it
        // into a fresh engine and check that the IL store reports the
        // predicate as promoted *before* any query runs.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("colors",
                ":- public color/1.\ncolor(red).\ncolor(green).\ncolor(blue)."),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);
        var loaded = BundleReader.FromBytes(bytes);

        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.LoadBundle(loaded);
        // Bundle load is lazy; pre-warm is now opt-in via compile_all /
        // WarmAllCompilable (front-loading what used to run at load).
        engine.WarmAllCompilable();

        int fid = FunctorId("color", 1);
        Assert.True(engine.IlPromotion.IsPromoted(fid),
            "color/1 should be IL-promoted after an explicit compile_all.");
    }

    [Fact]
    public void LoadBundle_WithoutBlob_LeavesPromotionUntouched()
    {
        // Same source but without the bytecode blob: LoadBundle still
        // consults the source, but no pre-warm happens. The IL store
        // remains empty until the counter-driven path fires.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("colors",
                ":- public color/1.\ncolor(red).\ncolor(green).\ncolor(blue)."),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: false);
        var loaded = BundleReader.FromBytes(bytes);

        var engine = new PrologEngine();
        engine.LoadBundle(loaded);

        int fid = FunctorId("color", 1);
        Assert.False(engine.IlPromotion.IsPromoted(fid));
    }

    [Fact]
    public void LoadBundle_PreWarmedPredicate_ProducesIdenticalResultsAsSource()
    {
        // Same source loaded two ways: with-blob (pre-warmed) vs.
        // without-blob (Tier 0 forever). Solutions and order must match.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("nums",
                ":- public num/1.\nnum(one).\nnum(two).\nnum(three)."),
        });
        byte[] withBlob = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);
        byte[] without = BundleWriter.ToBytes(bundle, includeCompiledBytecode: false);

        var enginePreWarm = new PrologEngine();
        enginePreWarm.LoadBundle(BundleReader.FromBytes(withBlob));
        var preWarmSols = enginePreWarm.QueryAll("num(X).").Select(s => s["X"]).ToList();

        var engineTier0 = new PrologEngine();
        engineTier0.LoadBundle(BundleReader.FromBytes(without));
        var tier0Sols = engineTier0.QueryAll("num(X).").Select(s => s["X"]).ToList();

        Assert.Equal(tier0Sols, preWarmSols);
    }

    [Fact]
    public void LoadBundle_PreWarm_UnsupportedPredicatesStayOnTier0()
    {
        // Predicates outside the IL subset (a body with non-tail
        // user-predicate calls — chunk 47 supports tail-call Execute
        // but not the intermediate Call opcode) survive the pre-warm
        // pass cleanly: the IL store marks them unpromotable, and the
        // source still runs via Tier 0.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("calls",
                ":- public bar/0.\nbar.\n" +
                ":- public baz/0.\nbaz.\n" +
                ":- public foo/0.\nfoo :- bar, baz."),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.LoadBundle(BundleReader.FromBytes(bytes));
        // Pre-warm is opt-in now (compile_all / WarmAllCompilable), not at load.
        engine.WarmAllCompilable();

        // bar/0 IS promotable (single-clause fact, no body).
        Assert.True(engine.IlPromotion.IsPromoted(FunctorId("bar", 0)));
        // foo/0 is NOT promotable (its body has a non-tail Call to
        // bar/0 before the tail-call to baz/0).
        Assert.True(engine.IlPromotion.IsUnpromotable(FunctorId("foo", 0)));
        // Both still work via the consulted source.
        Assert.True(engine.Query("foo.").Success);
    }

    [Fact]
    public void LoadBundle_PreWarm_FromDisk()
    {
        // End-to-end check: bundle → disk → load → pre-warm → query.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("greet",
                ":- public greet/1.\ngreet(world).\ngreet(prolog)."),
        });
        string path = Path.GetTempFileName();
        path = Path.ChangeExtension(path, ".shum");
        try
        {
            BundleWriter.WriteToFile(bundle, path, includeCompiledBytecode: true);
            var engine = new PrologEngine();
            engine.IlPromotion.Threshold = 1;
            engine.LoadBundle(path);
            // Pre-warm is opt-in now (compile_all / WarmAllCompilable).
            engine.WarmAllCompilable();
            Assert.True(engine.IlPromotion.IsPromoted(FunctorId("greet", 1)));
            Assert.True(engine.Query("greet(world).").Success);
            Assert.True(engine.Query("greet(prolog).").Success);
            Assert.False(engine.Query("greet(haskell).").Success);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

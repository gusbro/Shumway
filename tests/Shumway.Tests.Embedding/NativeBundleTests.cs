using System;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ADR-022 item 1 — embedded native blocks survive the separate-compilation /
// bundle pipeline. A DEBUG bundle keeps source and re-consults at load; a RELEASE
// (source-stripped) bundle runs the baked `'$native_run'('$nb$…', Vars)` dispatch
// against the engine's native-block table, repopulated from the bundle.
public sealed class NativeBundleTests
{
    private static class Interop
    {
        public static int strcmp(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));
        public static long sum(long a, long b) => a + b;
    }

    private const string CmpProgram =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- public cmp/3.\n" +
        ":- c.\nint strcmp(const char*, const char*);\n:- prolog.\n" +
        "cmp(A, B, R) :- atom(A), atom(B), { R is 'strcmp'(A, B) }, integer(R).\n";

    private static byte[] LinkBundle(string program, ShmoBuildMode mode, bool strip) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(program, "prog", mode) },
            EntryPoints = new[] { new PredicateRef("cmp", 3) },
            StripSource = strip,
            BakePrelude = true,
        }).Bytes!;

    [Fact]
    public void DebugBundle_NativeBlock_Runs()
    {
        var bytes = LinkBundle(CmpProgram, ShmoBuildMode.Debug, strip: false);
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));      // BEFORE load — the re-consult uses it
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
    }

    [Fact]
    public void ReleaseBundle_NativeBlock_Runs()
    {
        var bytes = LinkBundle(CmpProgram, ShmoBuildMode.Release, strip: true);
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
    }

    [Fact]
    public void ReleaseBundle_ArithmeticAndLocal_Runs()
    {
        // A block with an arithmetic output and a block-local intermediate (`T`),
        // exercising the marshalling of a computed long output through the bundle.
        const string program =
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- public calc/3.\n" +
            ":- c.\nlong sum(int, int);\n:- prolog.\n" +
            "calc(A, B, R) :- integer(A), integer(B), " +
            "{ T: long; T is 'sum'(A, B); R is T * 2 }, integer(R).\n";

        var bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(program, "prog", ShmoBuildMode.Release) },
            EntryPoints = new[] { new PredicateRef("calc", 3) },
            StripSource = true,
            BakePrelude = true,
        }).Bytes!;

        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.Equal(14L, e.Query("calc(3, 4, R).").Get<long>("R"));
    }

    [Theory]
    [InlineData(false)]   // --with-compiled-il
    [InlineData(true)]    // --with-compiled-il --strip-wam
    public void IlBundle_NativeBlock_Runs(bool stripWam)
    {
        // A persisted-IL bundle: the predicate's `$native_run` dispatch is baked
        // into IL; at run time it reaches NativeRun, which compiles the block (the
        // engine has the interop class) and runs it. Validates that build-time IL
        // bundles already run native blocks correctly via part 1, before the
        // inline optimization (part 2).
        var bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(CmpProgram, "prog", ShmoBuildMode.Release) },
            EntryPoints = new[] { new PredicateRef("cmp", 3) },
            StripSource = true,
            BakePrelude = true,
            IncludeCompiledIl = true,
            StripWam = stripWam,
        }).Bytes!;

        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
    }

    [Fact]
    public void Tier1Inline_NativeBlock_Runs()
    {
        // Item 2 stage C: when cmp/3 promotes to Tier-1 IL, its `$native_run` call
        // is inlined directly into the IL (the interop strcmp is a direct call).
        // Validates the inlined IL produces correct results.
        int before = Shumway.Compiler.Il.IlPredicateCompiler.NativeBlocksInlined;
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.IlPromotion.Threshold = 1;
        e.ConsultString(CmpProgram);
        for (int i = 0; i < 5; i++)
            Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
        Assert.True(e.Query("cmp(abc, abc, R), R == 0.").Success);
        Assert.True(e.Query("cmp(xyz, abc, R), R == 1.").Success);
        // Phase 33 I10 — wait for the background IL compile (which performs the
        // inline) to install before the count assertion (see
        // Tier1Inline_ArithmeticWithLocal_Runs for the full rationale — the shared
        // worker races under suite parallelism and `> before` would flake).
        Assert.True(e.IlPromotion.WaitForPendingPromotions(), "IL promotion did not complete");
        // confirm the block was actually inlined into IL (not run via dispatch).
        Assert.True(Shumway.Compiler.Il.IlPredicateCompiler.NativeBlocksInlined > before);
    }

    [Fact]
    public void Tier1Inline_ArithmeticWithLocal_Runs()
    {
        // A heavier inline: a block-local (T), a binary (T * 2) and two interop
        // calls, all promoted into the predicate's IL.
        const string program =
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- public calc/3.\n" +
            ":- c.\nlong sum(int, int);\n:- prolog.\n" +
            "calc(A, B, R) :- integer(A), integer(B), " +
            "{ T: long; T is 'sum'(A, B); R is T * 2 }, integer(R).\n";
        int before = Shumway.Compiler.Il.IlPredicateCompiler.NativeBlocksInlined;
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.IlPromotion.Threshold = 1;
        e.ConsultString(program);
        for (int i = 0; i < 5; i++)
            Assert.Equal(14L, e.Query("calc(3, 4, R).").Get<long>("R"));
        Assert.Equal(-2L, e.Query("calc(2, -3, R).").Get<long>("R"));
        // Phase 33 I10 — the inline happens inside the background IL compile
        // (the default path). Wait for it to install before asserting the counter:
        // otherwise, under suite parallelism, the queries above succeed on Tier-0
        // `$native_run` dispatch while the compile is still queued on the shared
        // worker, so NativeBlocksInlined hasn't bumped yet and `> before` flakes.
        // The results themselves are already verified above on whichever tier ran.
        Assert.True(e.IlPromotion.WaitForPendingPromotions(), "IL promotion did not complete");
        Assert.True(Shumway.Compiler.Il.IlPredicateCompiler.NativeBlocksInlined > before);
    }

    [Fact]
    public void BuildTimeInline_PersistedIlBundle_InlinesAndRuns()
    {
        // Item 2 stage C: a --with-compiled-il bundle inlines the native block at
        // BUILD time (the build engine auto-discovers Shumway.Native.Interop, so
        // the persisted IL emits a direct cross-assembly call). The block runs with
        // no $native_run dispatch; the load engine needs no UseNativeInterop because
        // the interop call was bound at build.
        int before = Shumway.Compiler.Il.IlPredicateCompiler.NativeBlocksInlined;
        var bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(CmpProgram, "prog", ShmoBuildMode.Release) },
            EntryPoints = new[] { new PredicateRef("cmp", 3) },
            StripSource = true,
            BakePrelude = true,
            IncludeCompiledIl = true,
        }).Bytes!;
        // the inline happened during the build (compiling the bundle's IL).
        Assert.True(Shumway.Compiler.Il.IlPredicateCompiler.NativeBlocksInlined > before);

        var e = new PrologEngine();   // note: no UseNativeInterop — the call is baked
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
        Assert.True(e.Query("cmp(abc, abc, R), R == 0.").Success);
    }

    // A `:- visible` predicate whose source clauses use native code (a real Arity
    // pattern — debug.pl's debug_msg/1). `:- visible` is dynamic (ISO-mutable), so
    // its clauses live in the dynamic store; the native block must still compile and
    // run — through both consult and a source-stripped bundle — as the predicate's
    // primed/evictable snapshot (ADR-023), not be rejected.
    private const string VisibleNativeProgram =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- visible cmp/3.\n" +
        ":- c.\nint strcmp(const char*, const char*);\n:- prolog.\n" +
        "cmp(A, B, R) :- atom(A), atom(B), { R is 'strcmp'(A, B) }, integer(R).\n";

    [Fact]
    public void VisiblePredicate_NativeBlock_Consult_Runs()
    {
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString(VisibleNativeProgram);
        Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
    }

    [Fact]
    public void VisiblePredicate_NativeBlock_Bundle_Runs()
    {
        var bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(VisibleNativeProgram, "prog", ShmoBuildMode.Release) },
            EntryPoints = new[] { new PredicateRef("cmp", 3) },
            StripSource = true,
            BakePrelude = true,
        }).Bytes!;
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
    }

    // ADR-022 — a scalar `:- c` global (`int counter;`) with Arity static-storage
    // semantics. The scalar-global metadata travels in the bundle (the `:- c`
    // declarations themselves do not), so persistence survives a source-stripped
    // bundle and the Tier-0 → Tier-1 IL transition.
    private const string CounterProgram =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- public incr/1.\n" +
        ":- c.\nint counter;\n:- prolog.\n" +
        "incr(X) :- { counter = counter + 1; X is counter }, integer(X).\n";

    [Fact]
    public void ReleaseBundle_ScalarGlobal_PersistsAcrossCalls()
    {
        var bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(CounterProgram, "prog", ShmoBuildMode.Release) },
            EntryPoints = new[] { new PredicateRef("incr", 1) },
            StripSource = true,
            BakePrelude = true,
        }).Bytes!;
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.Equal(1L, e.Query("incr(X).").Get<long>("X"));
        Assert.Equal(2L, e.Query("incr(X).").Get<long>("X"));   // persisted in the loaded bundle
        Assert.Equal(3L, e.Query("incr(X).").Get<long>("X"));
    }

    [Fact]
    public void Tier1Inline_ScalarGlobal_PersistsAcrossPromotion()
    {
        // The global must keep accumulating once the predicate promotes to Tier-1
        // IL (the inlined block seeds from / writes through the same per-engine
        // storage) — so the i-th call returns i across the Tier-0→Tier-1 boundary.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.IlPromotion.Threshold = 1;
        e.ConsultString(CounterProgram);
        for (long i = 1; i <= 8; i++)
            Assert.Equal(i, e.Query("incr(X).").Get<long>("X"));
    }

    [Fact]
    public void ReleaseBundle_MissingInterop_ThrowsAtRun()
    {
        // The bundle compiles (interop is not resolved at compile time), but a
        // block calling a function the running engine's interop class does not
        // provide must raise a HARD error when it runs — never silently no-op.
        var bytes = LinkBundle(CmpProgram, ShmoBuildMode.Release, strip: true);
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(EmptyInterop));   // no strcmp
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.ThrowsAny<Exception>(() => e.Query("cmp(abc, abd, R)."));
    }

    private static class EmptyInterop { }
}

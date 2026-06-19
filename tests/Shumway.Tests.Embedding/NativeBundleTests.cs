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

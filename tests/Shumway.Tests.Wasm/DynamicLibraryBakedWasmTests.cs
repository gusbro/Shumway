using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-075, decisions 12 and 13, on the wasm tier: a library bundle
/// with its WebAssembly baked. Its modules come in one at a time and run
/// their bytecode until the last one is in; then the baked module is
/// installed, and a call from one module into another is still a dispatch by
/// name.</summary>
public sealed class DynamicLibraryBakedWasmTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-dynlib-baked-" + Guid.NewGuid().ToString("N"));

    public DynamicLibraryBakedWasmTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ma and mb call each other; ma calls mq, which has module/2, by its
    // name.
    private static readonly (string Module, string Source)[] Modules =
    {
        ("ma", @"
            pa(1).
            pa(2).
            ca(X) :- pb(X).
            ca_sum(S) :- findall(X, pb(X), L), sum_list(L, S).
            ca_last(X, Y) :- pa(X), pb(Y).
            ca_q(X) :- mq:q(X), X > 0.
            "),
        ("mb", @"
            pb(10).
            pb(20).
            cb(X) :- pa(X).
            cb_twice(X, Y) :- pa(X), Y is X * 2.
            "),
        ("mq", @"
            :- module(mq, [q/1]).
            q(X) :- helper(X).
            helper(5).
            "),
    };

    // Each succeeds with the right answer only.
    public static IEnumerable<object[]> Goals() => new[]
    {
        new object[] { "ca(X), X == 10" },
        new object[] { "ca_sum(S), S == 30" },
        new object[] { "findall(X-Y, ca_last(X, Y), L), L == [1-10, 1-20, 2-10, 2-20]" },
        new object[] { "ca_q(X), X == 5" },
        new object[] { "cb(X), X == 1" },
        new object[] { "findall(X-Y, cb_twice(X, Y), L), L == [1-2, 2-4]" },
    };

    private void BuildLibrary() => BuildLibrary("baked", Modules);

    private void BuildLibrary(string name, (string Module, string Source)[] modules)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = modules.Select(m => ShmoCompiler.CompileSource(m.Source, m.Module)).ToArray(),
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
            WasmBaker = b => WasmBundleTier.Bake(b, stdlib: false),
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(BundleReader.FromBytes(link.Bytes!).WasmModules);
        File.WriteAllBytes(Path.Combine(_dir, name + ".shum"), link.Bytes!);
    }

    // A tier that installs a bundle's baked modules and compiles nothing of
    // its own.
    private PrologEngine Engine()
    {
        var world = new DesktopWasmWorld();
        var engine = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes, linked) => WasmBundleTier.Install(eng, world, bytes, linked),
        };
        engine.AddLibraryDirectory(_dir);
        return engine;
    }

    private static bool Installed(PrologEngine e, string name) =>
        e.IlPromotion.Wasm!.BundleFids.Any(f => RelocatingCompileEnv.NameOf(f).Name == name);

    // The first call into one module, which calls another that has not come
    // in, from a query of its own and from the query that attaches.
    [Theory]
    [MemberData(nameof(Goals))]
    public void ACallIntoAModule_BringsInWhatItCalls_AndAnswers(string goal)
    {
        BuildLibrary();

        var separate = Engine();
        Assert.True(separate.Query("ensure_loaded(bundle(baked)).").Success);
        Assert.True(separate.Query(goal + ".").Success, goal);
        Assert.True(separate.Query(goal + ".").Success, "the next query");

        var one = Engine();
        Assert.True(one.Query($"ensure_loaded(bundle(baked)), {goal}.").Success, "in the query that attaches");
    }

    // The baked module waits for the last module of the library; once it is
    // installed, every call answers as before, the ones that cross from one
    // module into another too.
    [Fact]
    public void TheBakedModule_IsInstalledWhenTheLastModuleComesIn()
    {
        BuildLibrary();
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(baked)).").Success);
        Assert.True(e.Query("ca(X), X == 10.").Success);
        Assert.False(Installed(e, "ma$ca"), "installed before mq came in");

        Assert.True(e.Query("ca_q(X), X == 5.").Success);
        Assert.False(e.IsCodePending("mq"));
        Assert.True(e.Query("true.").Success);
        Assert.True(Installed(e, "ma$ca"), "not installed once every module was in");

        foreach (var goal in Goals().Select(g => (string)g[0]))
            Assert.True(e.Query(goal + ".").Success, goal);
    }

    // A library whose one module has initialization goals, as a runtime: the
    // attach brings it in, and the next query installs its baked module.
    [Fact]
    public void ALibraryWhoseModulesComeInAtTheAttach_InstallsItsBakedModule()
    {
        BuildLibrary("runtime", new[]
        {
            ("rt", @"
                :- dynamic(started/0).
                :- initialization(assertz(started)).
                rp(X) :- started, X = 7.
                "),
        });
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(runtime)).").Success);
        Assert.False(e.IsCodePending("rt"));
        Assert.True(e.Query("rp(X), X == 7.").Success);
        Assert.True(Installed(e, "rt$rp"), "not installed after the attach");
    }
}

using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-075, decisions 12 and 13, on the wasm tier: a library bundle
/// with its WebAssembly baked. The module installs with the modules of the
/// library that are in and again as each comes in, and a call from one
/// module into another goes through the table, into a module that has not
/// come in too.</summary>
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

    private void BuildLibrary(string name, (string Module, string Source)[] modules, bool library = true)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = modules.Select(m => ShmoCompiler.CompileSource(m.Source, m.Module)).ToArray(),
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
            WasmBaker = b => WasmBundleTier.Bake(b, stdlib: false, library: library),
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

    // A module's baked code is installed when it comes in, the others' not.
    // Running there, it calls into a module that has not come in, which the
    // call brings in; the next query installs that one's code.
    [Fact]
    public void EachModulesBakedCode_IsInstalledWhenItComesIn()
    {
        BuildLibrary();
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(baked)).").Success);
        Assert.True(e.Query("pa(1).").Success);
        Assert.True(e.Query("true.").Success);
        Assert.True(Installed(e, "ma$ca"), "ma's code not installed once ma came in");
        Assert.True(e.IsCodePending("mb"));
        Assert.False(Installed(e, "mb$cb"), "mb's code installed before mb came in");

        Assert.True(e.Query("ca(X), X == 10.").Success);
        Assert.False(e.IsCodePending("mb"));
        Assert.True(e.Query("true.").Success);
        Assert.True(Installed(e, "mb$cb"), "mb's code not installed once mb came in");
        Assert.False(Installed(e, "mq$helper"), "mq's code installed before mq came in");

        foreach (var goal in Goals().Select(g => (string)g[0]))
            Assert.True(e.Query(goal + ".").Success, goal);
        Assert.True(e.Query("true.").Success);
        Assert.True(Installed(e, "mq$helper"), "mq's code not installed once mq came in");
        Assert.Empty(e.IlPromotion.PartialWasmModules);
    }

    private static readonly (string Module, string Source)[] Imports =
    {
        ("mi", @"
            :- use_module(library(mexp)).
            cm(1).
            ci(X) :- ex(X).
            ci_all(L) :- findall(X, ex(X), L).
            "),
        ("mexp", @"
            :- module(mexp, [ex/1]).
            ex(1).
            ex(2).
            "),
    };

    // mi imports mexp's ex/1, and the call is linked to it. Baked in one
    // module it would be a jump; between two modules of a library it goes
    // through the table, which has no row for ex/1 until mexp is in: the
    // call brings mexp in, and the code of mi never runs mexp's unlinked.
    [Fact]
    public void AnImportedCall_FromBakedCode_BringsInTheModule()
    {
        BuildLibrary("imports", Imports);
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(imports)).").Success);
        Assert.True(e.Query("cm(1).").Success);
        Assert.True(e.Query("true.").Success);
        Assert.True(Installed(e, "mi$ci"), "mi's code not installed once mi came in");
        Assert.True(e.IsCodePending("mexp"));

        Assert.True(e.Query("ci(X), X == 1.").Success);
        Assert.False(e.IsCodePending("mexp"));
        Assert.True(e.Query("true.").Success);
        Assert.True(Installed(e, "mexp$ex"), "mexp's code not installed once mexp came in");
        Assert.True(e.Query("ci_all(L), L == [1, 2].").Success);
        Assert.True(e.Query("findall(X, ci(X), L), L == [1, 2].").Success);
    }

    // The goal that brings a module in installs its code, with no query
    // after it, as a notebook's server is one query: mi in the goal that
    // attaches, mexp in the next one, with mi's.
    [Fact]
    public void AModulesBakedCode_IsInstalledInTheGoalThatBringsItIn()
    {
        BuildLibrary("imports", Imports);
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(imports)), cm(1).").Success);
        Assert.True(Installed(e, "mi$ci"), "mi's code not installed in the goal that brought mi in");
        Assert.True(e.IsCodePending("mexp"));

        Assert.True(e.Query("ci_all(L), L == [1, 2].").Success);
        Assert.True(Installed(e, "mexp$ex"), "mexp's code not installed in the goal that brought mexp in");
        Assert.True(Installed(e, "mi$ci"), "mi's code gone when mexp's came in");
        Assert.Empty(e.IlPromotion.PartialWasmModules);
        // Installed against the goal's link: the next query setup installs
        // it again against the program's.
        Assert.Single(e.IlPromotion.PendingWasmModules);
        Assert.True(e.Query("findall(X, ci(X), L), L == [1, 2].").Success);
        Assert.Empty(e.IlPromotion.PendingWasmModules);
        Assert.True(Installed(e, "mi$ci") && Installed(e, "mexp$ex"), "not installed whole");
    }

    // Baked as a link that is not a library, mi's call to ex/1 is a jump:
    // attached, the module waits for every module, and then installs.
    [Fact]
    public void AModuleBakedForAWholeLoad_InstallsOnceEveryModuleIsIn()
    {
        BuildLibrary("whole", Imports, library: false);
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(whole)).").Success);
        Assert.True(e.Query("cm(1).").Success);
        Assert.True(e.Query("true.").Success);
        Assert.False(Installed(e, "mi$ci"), "installed before mexp came in");

        Assert.True(e.Query("ci_all(L), L == [1, 2].").Success);
        Assert.True(e.Query("true.").Success);
        Assert.True(Installed(e, "mi$ci"), "not installed once every module was in");
        Assert.True(e.Query("findall(X, ci(X), L), L == [1, 2].").Success);
        Assert.Empty(e.IlPromotion.PartialWasmModules);
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

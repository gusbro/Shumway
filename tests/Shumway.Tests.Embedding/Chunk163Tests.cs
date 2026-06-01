using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 13 chunk 163: the linker. Resolves a set of compiled
/// <c>.shmo</c> objects into a deployable <see cref="Bundle"/> by
/// walking reachability from entry points + <c>ensure_linked</c>
/// roots, reporting missing predicates and duplicate-public
/// collisions, and dropping unreachable modules.
/// </summary>
public class Chunk163Tests
{
    private static ShmoObject Compile(string source, string moduleFallback = "user")
        => ShmoCompiler.CompileSource(source, moduleFallback);

    [Fact]
    public void Link_SingleModule_EntryReachable()
    {
        var obj = Compile(
            ":- module(m1).\n:- public start/0.\nstart :- helper.\nhelper.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("start", 0) },
        });
        Assert.True(result.Success);
        Assert.NotNull(result.Bundle);
        Assert.Contains("m1", result.ReachedModules);
        Assert.Contains(new PredicateRef("start", 0), result.ReachedPredicates);
        Assert.Contains(new PredicateRef("helper", 0), result.ReachedPredicates);
        Assert.Empty(result.MissingPredicates);
    }

    [Fact]
    public void Link_MissingPredicate_Errors()
    {
        var obj = Compile(
            ":- module(m1).\n:- public start/0.\nstart :- nonexistent.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("start", 0) },
        });
        Assert.False(result.Success);
        Assert.Contains(new PredicateRef("nonexistent", 0), result.MissingPredicates);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == LinkSeverity.Error && d.Code == "missing_predicate");
    }

    [Fact]
    public void Link_MissingPredicate_AllowUndefined_DowngradesToWarning()
    {
        var obj = Compile(
            ":- module(m1).\n:- public start/0.\nstart :- nonexistent.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("start", 0) },
            AllowUndefined = true,
        });
        Assert.True(result.Success);
        Assert.NotNull(result.Bundle);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == LinkSeverity.Warning && d.Code == "missing_predicate");
    }

    [Fact]
    public void Link_CrossModule_PublicResolves()
    {
        var libObj = Compile(
            ":- module(lib).\n:- public lib_helper/1.\nlib_helper(X) :- X = ok.\n");
        var appObj = Compile(
            ":- module(app).\n:- public main/1.\nmain(X) :- lib_helper(X).\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { libObj, appObj },
            EntryPoints = new[] { new PredicateRef("main", 1) },
        });
        Assert.True(result.Success);
        Assert.Contains("app", result.ReachedModules);
        Assert.Contains("lib", result.ReachedModules);
    }

    [Fact]
    public void Link_CrossModule_LocalNotVisible_Missing()
    {
        // lib defines helper/1 as LOCAL, app tries to call it.
        var libObj = Compile(
            ":- module(lib).\n:- public lib_main/0.\nlib_main :- helper(1).\nhelper(_).\n");
        var appObj = Compile(
            ":- module(app).\n:- public main/0.\nmain :- helper(2).\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { libObj, appObj },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.False(result.Success);
        Assert.Contains(new PredicateRef("helper", 1), result.MissingPredicates);
    }

    [Fact]
    public void Link_DuplicatePublic_AcrossModules_Errors()
    {
        var a = Compile(":- module(a).\n:- public foo/1.\nfoo(1).\n");
        var b = Compile(":- module(b).\n:- public foo/1.\nfoo(2).\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { a, b },
            EntryPoints = new[] { new PredicateRef("foo", 1) },
        });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == LinkSeverity.Error && d.Code == "duplicate_public");
    }

    [Fact]
    public void Link_DynamicAcrossModules_NotACollision()
    {
        var a = Compile(":- module(a).\n:- public init_a/0.\n:- dynamic shared/1.\ninit_a :- assertz(shared(1)).\n");
        var b = Compile(":- module(b).\n:- public init_b/0.\n:- dynamic shared/1.\ninit_b :- assertz(shared(2)).\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { a, b },
            EntryPoints = new[] { new PredicateRef("init_a", 0), new PredicateRef("init_b", 0) },
        });
        var diags = string.Join("\n", result.Diagnostics.Select(d => $"{d.Severity}/{d.Code}: {d.Message}"));
        Assert.True(result.Success, diags);
    }

    [Fact]
    public void Link_EnsureLinked_KeepsModuleReachable()
    {
        // dispatcher uses call/1 with a runtime-constructed goal —
        // the static call graph won't show the dispatch to handler/1
        // unless handler is marked ensure_linked.
        var dispatcher = Compile(
            ":- module(disp).\n:- public dispatch/1.\n:- ensure_linked handler/1.\n"
            + "dispatch(X) :- G =.. [handler, X], call(G).\n");
        var handler = Compile(
            ":- module(h).\n:- public handler/1.\nhandler(_).\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { dispatcher, handler },
            EntryPoints = new[] { new PredicateRef("dispatch", 1) },
        });
        Assert.True(result.Success);
        Assert.Contains("h", result.ReachedModules);
    }

    [Fact]
    public void Link_UnreachableModule_DroppedWithWarning()
    {
        var used = Compile(":- module(used).\n:- public foo/0.\nfoo.\n");
        var dead = Compile(":- module(dead).\n:- public bar/0.\nbar.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { used, dead },
            EntryPoints = new[] { new PredicateRef("foo", 0) },
        });
        Assert.True(result.Success);
        Assert.Contains("dead", result.UnreachableModules);
        Assert.Contains(result.Diagnostics,
            d => d.Code == "unreachable_module" && d.Severity == LinkSeverity.Warning);
        Assert.Single(result.Bundle!.Entries);
        Assert.Equal("used", result.Bundle!.Entries[0].ModuleName);
    }

    [Fact]
    public void Link_BuiltinCalls_DoNotShowAsMissing()
    {
        var obj = Compile(
            ":- module(m).\n:- public f/1.\n"
            + "f(X) :- Y is X + 1, write(Y), nl.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("f", 1) },
        });
        Assert.True(result.Success);
        Assert.Empty(result.MissingPredicates);
    }

    [Fact]
    public void Link_PreludeCalls_DoNotShowAsMissing()
    {
        // member/2 is in the prelude — should resolve.
        var obj = Compile(
            ":- module(m).\n:- public has_x/1.\nhas_x(L) :- member(x, L).\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("has_x", 1) },
        });
        Assert.True(result.Success);
    }

    [Fact]
    public void Link_EntryNotFound_Errors()
    {
        var obj = Compile(":- module(m).\n:- public foo/0.\nfoo.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("not_here", 2) },
        });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Code == "entry_not_found" && d.Severity == LinkSeverity.Error);
    }

    [Fact]
    public void Link_QualifiedRef_ResolvesAgainstNamedModule()
    {
        var lib = Compile(":- module(lib).\n:- public ext/1.\next(ok).\n");
        var app = Compile(":- module(app).\n:- public main/0.\nmain :- lib:ext(_).\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { lib, app },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.True(result.Success);
        Assert.Contains("lib", result.ReachedModules);
    }

    [Fact]
    public void Link_QualifiedRef_TargetNotPublic_Missing()
    {
        // lib defines secret/0 as LOCAL; app tries qualified call.
        var lib = Compile(":- module(lib).\nsecret.\n");
        var app = Compile(":- module(app).\n:- public main/0.\nmain :- lib:secret.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { lib, app },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "missing_predicate");
    }

    [Fact]
    public void Link_BundleBytes_LoadsAndExecutes()
    {
        var obj = Compile(
            ":- module(m).\n:- public answer/1.\nanswer(42).\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("answer", 1) },
        });
        Assert.True(result.Success);
        Assert.NotNull(result.Bytes);

        // Load the bundle and verify the query runs.
        var bundle = BundleReader.FromBytes(result.Bytes!);
        var engine = new PrologEngine();
        engine.LoadBundle(bundle);
        var sol = engine.Query("answer(X).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Link_EnsureLinked_PullsInOtherwiseUnreachableModule()
    {
        // 'app' never directly references 'plugin' via call graph;
        // only ensure_linked saves it from dead-code elimination.
        var app = Compile(
            ":- module(app).\n:- public main/0.\n:- ensure_linked plugin_run/0.\nmain.\n");
        var plugin = Compile(
            ":- module(plugin).\n:- public plugin_run/0.\nplugin_run.\n");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { app, plugin },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.True(result.Success);
        Assert.Contains("plugin", result.ReachedModules);
        Assert.DoesNotContain("plugin", result.UnreachableModules);
    }
}

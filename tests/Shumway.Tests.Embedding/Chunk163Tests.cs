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
        var obj = Compile("""
            :- module(m1).
            :- public start/0.
            start :- helper.
            helper.
            """);
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
        var obj = Compile("""
            :- module(m1).
            :- public start/0.
            start :- nonexistent.
            """);
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
        var obj = Compile("""
            :- module(m1).
            :- public start/0.
            start :- nonexistent.
            """);
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
        var libObj = Compile("""
            :- module(lib).
            :- public lib_helper/1.
            lib_helper(X) :- X = ok.
            """);
        var appObj = Compile("""
            :- module(app).
            :- public main/1.
            main(X) :- lib_helper(X).
            """);
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
        var libObj = Compile("""
            :- module(lib).
            :- public lib_main/0.
            lib_main :- helper(1).
            helper(_).
            """);
        var appObj = Compile("""
            :- module(app).
            :- public main/0.
            main :- helper(2).
            """);
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
        var a = Compile("""
            :- module(a).
            :- public foo/1.
            foo(1).
            """);
        var b = Compile("""
            :- module(b).
            :- public foo/1.
            foo(2).
            """);
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
        var a = Compile("""
            :- module(a).
            :- public init_a/0.
            :- dynamic shared/1.
            init_a :- assertz(shared(1)).
            """);
        var b = Compile("""
            :- module(b).
            :- public init_b/0.
            :- dynamic shared/1.
            init_b :- assertz(shared(2)).
            """);
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
        var dispatcher = Compile("""
            :- module(disp).
            :- public dispatch/1.
            :- ensure_linked handler/1.
            dispatch(X) :- G =.. [handler, X], call(G).
            """);
        var handler = Compile("""
            :- module(h).
            :- public handler/1.
            handler(_).
            """);
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
        var used = Compile("""
            :- module(used).
            :- public foo/0.
            foo.
            """);
        var dead = Compile("""
            :- module(dead).
            :- public bar/0.
            bar.
            """);
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
        var obj = Compile("""
            :- module(m).
            :- public f/1.
            f(X) :- Y is X + 1, write(Y), nl.
            """);
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
        var obj = Compile("""
            :- module(m).
            :- public has_x/1.
            has_x(L) :- member(x, L).
            """);
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
        var obj = Compile("""
            :- module(m).
            :- public foo/0.
            foo.
            """);
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
        var lib = Compile("""
            :- module(lib).
            :- public ext/1.
            ext(ok).
            """);
        var app = Compile("""
            :- module(app).
            :- public main/0.
            main :- lib:ext(_).
            """);
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
        var lib = Compile("""
            :- module(lib).
            secret.
            """);
        var app = Compile("""
            :- module(app).
            :- public main/0.
            main :- lib:secret.
            """);
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
        var obj = Compile("""
            :- module(m).
            :- public answer/1.
            answer(42).
            """);
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
        var app = Compile("""
            :- module(app).
            :- public main/0.
            :- ensure_linked plugin_run/0.
            main.
            """);
        var plugin = Compile("""
            :- module(plugin).
            :- public plugin_run/0.
            plugin_run.
            """);
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

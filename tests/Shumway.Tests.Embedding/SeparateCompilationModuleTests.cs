using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-038 Component 3 — export-qualified modules through separate compilation:
/// shumway-compile (ShmoCompiler) each module, shumway-link (ShmoLinker) them,
/// load the bundle, run. A two-arg :- module(Name, [Exports]) compiles as
/// export-qualified (mangled Name$x); a use_module(library(X), [Filter]) import
/// is resolved to X$pred in the importer's bytecode, and the linker reaches the
/// source module through the carried import table.
/// </summary>
public sealed class SeparateCompilationModuleTests
{
    private static LinkResult Link(ShmoObject[] objects, params (string, int)[] entries)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = entries.Select(e => new PredicateRef(e.Item1, e.Item2)).ToArray(),
        });

    private const string GreetQ =
        ":- module(greetq, [hello/1]).\n" +
        "hello(world).\n" +
        "secret(hidden).\n";

    [Fact]
    public void ExportQualifiedModule_CompilesMangled_NotBareGlobal()
    {
        var obj = ShmoCompiler.CompileSource(GreetQ, "greetq");
        Assert.True(obj.IsExportQualified);
        Assert.Contains(new PredicateRef("hello", 1), obj.Exports);
        // hello/1 and secret/1 are Local (mangled), NOT Public — an
        // export-qualified module contributes nothing bare-global.
        Assert.All(obj.Defined, d => Assert.Equal(PredicateVisibility.Local, d.Visibility));
    }

    [Fact]
    public void FilteredImport_ResolvesThroughSeparateLink()
    {
        var greetq = ShmoCompiler.CompileSource(GreetQ, "greetq");
        // A bare-global program (module app, one-arg) that imports hello/1 from
        // the export-qualified greetq. run/1 stays bare-global (public), so it is
        // a normal entry point and queryable bare; its body call to hello is
        // resolved to greetq$hello.
        var main = ShmoCompiler.CompileSource(
            ":- module(app).\n" +
            ":- public run/1.\n" +
            ":- use_module(library(greetq), [hello/1]).\n" +
            "run(X) :- hello(X).\n", "app");

        var r = Link(new[] { main, greetq }, ("run", 1));
        Assert.True(r.Success);
        Assert.Contains("greetq", r.ReachedModules);

        var engine = new PrologEngine();
        engine.LoadBundle(r.Bundle!);
        Assert.True(engine.Query("run(world).").Success);
        // secret/1 was neither exported nor imported → not reachable bare.
        Assert.False(engine.Query("catch(secret(hidden), _, fail).").Success);
    }

    [Fact]
    public void VariableMetaCall_ResolvesThroughLoadedBundleImportTable()
    {
        var greetq = ShmoCompiler.CompileSource(GreetQ, "greetq");
        // The body is a VARIABLE meta-call, so resolution runs through the
        // runtime $mqual import path — which needs the bundle to have carried the
        // import table into the reconstructed manifest (ADR-038 Component 3b).
        var main = ShmoCompiler.CompileSource(
            ":- module(app).\n" +
            ":- public run/1.\n" +
            ":- use_module(library(greetq), [hello/1]).\n" +
            "run(X) :- G = hello(X), call(G).\n", "app");

        var r = Link(new[] { main, greetq }, ("run", 1));
        Assert.True(r.Success);

        // Round-trip through .shum bytes so the manifest reconstruction is exercised.
        byte[] shum = BundleWriter.ToBytes(r.Bundle!);
        Bundle back = BundleReader.FromBytes(shum);

        var engine = new PrologEngine();
        engine.LoadBundle(back);
        Assert.True(engine.Query("run(world).").Success);
    }

    [Fact]
    public void LinkerPullsLibraryDependencyFromSearchPath()
    {
        // greetq is NOT passed to the linker; it is dropped as greetq.pl on a
        // library search dir and pulled in on demand (C-linker style).
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "shumway-linkpull-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "greetq.pl"), GreetQ);
            var main = ShmoCompiler.CompileSource(
                ":- module(app).\n" +
                ":- public run/1.\n" +
                ":- use_module(library(greetq), [hello/1]).\n" +
                "run(X) :- hello(X).\n", "app");

            var r = ShmoLinker.Link(new LinkConfig
            {
                Objects = new[] { main },
                EntryPoints = new[] { new PredicateRef("run", 1) },
                LibraryDirs = new[] { dir },
            });
            Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
            Assert.Contains("greetq", r.ReachedModules);

            var engine = new PrologEngine();
            engine.LoadBundle(r.Bundle!);
            Assert.True(engine.Query("run(world).").Success);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ImportAll_ResolvesExportsAtCompileTime()
    {
        // /1 import-all through separate compilation: ShmoCompiler reads greetq's
        // export surface from greetq.pl on the compile-time library path, so the
        // importer's bare hello call mangles to greetq$hello with no explicit
        // filter.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "shumway-imp1-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "greetq.pl"), GreetQ);
            var main = ShmoCompiler.CompileSource(
                ":- module(app).\n" +
                ":- public run/1.\n" +
                ":- use_module(library(greetq)).\n" +
                "run(X) :- hello(X).\n",
                "app", libraryDirs: new[] { dir });
            // The import table was resolved at compile time.
            Assert.Contains(main.Imports, e => e.Pred == new PredicateRef("hello", 1)
                && e.Source == "greetq");

            var r = ShmoLinker.Link(new LinkConfig
            {
                Objects = new[] { main },
                EntryPoints = new[] { new PredicateRef("run", 1) },
                LibraryDirs = new[] { dir },
            });
            Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));

            var engine = new PrologEngine();
            engine.LoadBundle(r.Bundle!);
            Assert.True(engine.Query("run(world).").Success);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnresolvedLibrary_ReportsTheLibraryNotEachPredicate()
    {
        // A program importing a library that is neither passed nor on any search
        // path must fail naming the LIBRARY (the root cause), not degrade into a
        // confusing missing-predicate error for each imported predicate.
        var main = ShmoCompiler.CompileSource(
            ":- module(app).\n" +
            ":- public run/1.\n" +
            ":- use_module(library(greetq), [hello/1]).\n" +
            "run(X) :- hello(X).\n", "app");

        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { main },
            EntryPoints = new[] { new PredicateRef("run", 1) },
            // no greetq object, no libraries, no LibraryDirs
        });
        Assert.False(r.Success);
        Assert.Contains(r.Diagnostics, d => d.Code == "unresolved_library"
            && d.Message.Contains("greetq"));
    }

    [Fact]
    public void ImportShmoCarriesImportTable()
    {
        var main = ShmoCompiler.CompileSource(
            ":- module(app, [run/1]).\n" +
            ":- use_module(library(greetq), [hello/1]).\n" +
            "run(X) :- hello(X).\n", "app");
        Assert.Contains(main.Imports, e => e.Pred == new PredicateRef("hello", 1)
            && e.Source == "greetq");
        Assert.Contains(main.LibraryDeps, d => d.LibName == "greetq" && !d.Baked);
    }

    [Fact]
    public void ShmoObject_RoundTripsExportQualification()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(app, [run/1]).\n" +
            ":- use_module(library(greetq), [hello/1]).\n" +
            "run(X) :- hello(X).\n", "app");
        byte[] bytes = ShmoWriter.ToBytes(obj);
        ShmoObject back = ShmoReader.FromBytes(bytes);
        Assert.True(back.IsExportQualified);
        Assert.Equal(obj.Exports, back.Exports);
        Assert.Equal(obj.Imports, back.Imports);
        Assert.Equal(obj.LibraryDeps.Select(d => d.LibName),
                     back.LibraryDeps.Select(d => d.LibName));
    }
}

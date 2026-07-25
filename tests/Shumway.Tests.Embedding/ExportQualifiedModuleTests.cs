using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-038 Component 2 — export-qualified modules (:- module(Name, [Exports]))
/// and per-module import tables. Every predicate of such a module is mangled
/// Name$x; use_module imports build an import table so a caller resolves an
/// imported p/N to Source$p/N; two such modules can export the same name.
/// </summary>
public class ExportQualifiedModuleTests
{
    private sealed class LibSet : System.IDisposable
    {
        public string Dir { get; }
        public LibSet()
        {
            Dir = Path.Combine(Path.GetTempPath(),
                "shumway-modtest-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }
        public LibSet Add(string libName, string source)
        {
            File.WriteAllText(Path.Combine(Dir, libName + ".pl"), source);
            return this;
        }
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static PrologEngine EngineWith(LibSet libs)
    {
        var e = new PrologEngine();
        e.AddLibraryDirectory(libs.Dir);
        return e;
    }

    // Succeeds iff goal has a solution, false on failure OR existence_error.
    private static bool Holds(PrologEngine e, string goal) =>
        e.Query($"catch(({goal}), _, fail).").Success;

    private const string GreetQ =
        ":- module(greetq, [hello/1]).\n" +
        "hello(world).\n" +
        "secret(hidden).\n";

    [Fact]
    public void ImportAll_ResolvesExportedPredicate()
    {
        using var libs = new LibSet().Add("greetq", GreetQ);
        var e = EngineWith(libs);
        e.ConsultString(
            ":- use_module(library(greetq)).\n" +
            "main(X) :- hello(X).");
        Assert.True(e.Query("main(world).").Success);
    }

    [Fact]
    public void NonExportedPredicate_IsInvisibleToImporter()
    {
        using var libs = new LibSet().Add("greetq", GreetQ);
        var e = EngineWith(libs);
        e.ConsultString(
            ":- use_module(library(greetq)).\n" +
            "peek(X) :- secret(X).");
        // secret/1 is defined in greetq but not exported → not imported → the
        // bare call finds no predicate (existence_error, caught to false).
        Assert.False(Holds(e, "peek(hidden)"));
    }

    [Fact]
    public void ImportFilter_ImportsOnlySelected()
    {
        using var libs = new LibSet().Add("greetq",
            ":- module(greetq, [hello/1, bye/1]).\n" +
            "hello(world).\n" +
            "bye(gone).\n");
        var e = EngineWith(libs);
        e.ConsultString(
            ":- use_module(library(greetq), [hello/1]).\n" +
            "a(X) :- hello(X).\n" +
            "b(X) :- bye(X).");
        Assert.True(e.Query("a(world).").Success);
        Assert.False(Holds(e, "b(gone)"));   // bye/1 not imported
    }

    [Fact]
    public void ImportOfNonExport_IsAnError()
    {
        using var libs = new LibSet().Add("greetq", GreetQ);
        var e = EngineWith(libs);
        var ex = Record.Exception(() => e.ConsultString(
            ":- use_module(library(greetq), [secret/1]).\n" +
            "p(X) :- secret(X)."));
        Assert.NotNull(ex);
        Assert.Contains("secret", ex!.Message);
    }

    [Fact]
    public void ExportQualifiedModule_CallsBareGlobalPreludeWithoutImport()
    {
        // check/1 uses member/2 (prelude, bare-global) with NO
        // use_module(library(lists)) — the bare-global fallthrough covers it.
        using var libs = new LibSet().Add("usesmember",
            ":- module(usesmember, [check/2]).\n" +
            "check(E, L) :- member(E, L).\n");
        var e = EngineWith(libs);
        e.ConsultString(
            ":- use_module(library(usesmember)).\n" +
            "run(E, L) :- check(E, L).");
        Assert.True(e.Query("run(2, [1,2,3]).").Success);
        Assert.False(e.Query("run(9, [1,2,3]).").Success);
    }

    [Fact]
    public void VariableMetaCall_ResolvesThroughImportTable()
    {
        using var libs = new LibSet().Add("greetq", GreetQ);
        var e = EngineWith(libs);
        // The body call is a VARIABLE meta-call (call(G) with G bound at runtime)
        // — resolution goes through the runtime $mqual import path, not the
        // compile-time ModuleRewrite one.
        e.ConsultString(
            ":- use_module(library(greetq)).\n" +
            "run(X) :- G = hello(X), call(G).");
        Assert.True(e.Query("run(world).").Success);
    }

    [Fact]
    public void SameNameExports_CoexistWithoutCollision()
    {
        using var libs = new LibSet()
            .Add("liba", ":- module(liba, [foo/1]).\nfoo(from_a).\n")
            .Add("libb", ":- module(libb, [foo/1]).\nfoo(from_b).\n");

        var ea = EngineWith(libs);
        ea.ConsultString(":- use_module(library(liba)).\nget(X) :- foo(X).");
        Assert.Equal("from_a", ea.QueryFirst<string>("get(X).", "X"));

        var eb = EngineWith(libs);
        eb.ConsultString(":- use_module(library(libb)).\nget(X) :- foo(X).");
        Assert.Equal("from_b", eb.QueryFirst<string>("get(X).", "X"));
    }

    [Fact]
    public void BothSameNameLibraries_LoadIntoOneEngineWithoutDuplicatePublic()
    {
        // liba and libb both export foo/1; loading both in one engine must not
        // trip ValidatePublicUniqueness (they mangle to liba$foo / libb$foo).
        using var libs = new LibSet()
            .Add("liba", ":- module(liba, [foo/1]).\nfoo(from_a).\n")
            .Add("libb", ":- module(libb, [foo/1]).\nfoo(from_b).\n");
        var e = EngineWith(libs);
        e.ConsultString(
            ":- use_module(library(liba)).\n" +
            ":- use_module(library(libb)).\n" +
            "get(X) :- foo(X).");
        // liba was imported first; foo resolves to liba$foo.
        Assert.Equal("from_a", e.QueryFirst<string>("get(X).", "X"));
    }
}

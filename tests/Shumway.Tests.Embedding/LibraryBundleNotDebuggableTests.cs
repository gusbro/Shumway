using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035: a library imported from a bundle is code the user did
/// not write, as clpfd is. Its code is release, but the interpreter raises a
/// port at every call whatever the callee was compiled from, and a step stops
/// at any port in a predicate not marked otherwise: Step Into went into the
/// library, at a file and line nobody can show.</summary>
public sealed class LibraryBundleNotDebuggableTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_libnodebug_" + Guid.NewGuid().ToString("N"));

    public LibraryBundleNotDebuggableTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ADebuggerDoesNotStopInALibraryBundle()
    {
        var obj = ShmoCompiler.CompileSource("""
            :- module(dbglib, [twice/2, big/2]).
            twice(X, Y) :- dbl(X, Y).
            dbl(X, Y) :- Y is X * 2.
            big(L, B) :- findall(X, (member(X, L), X > 1), B).
            """, "dbglib");
        File.WriteAllBytes(Path.Combine(_dir, "dbglib.shum"), Librarian.CreateArchive(new[]
        {
            new BundleArchiveMember("dbglib.shmo", ShmoWriter.ToBytes(obj)),
        }));

        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        e.ConsultString(":- set_prolog_flag(compile_mode, debug).");
        e.ConsultString("""
            :- use_module(library(dbglib)).
            go(Y) :- twice(3, Y), big([1,2,3], _).
            """);
        Assert.True(e.Query("go(6).").Success);

        var library = new List<string>();
        bool sawGo = false;
        foreach (var pred in e._staticLink!.PredicatesByAddress.Values)
        {
            var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
            string name = AtomTable.GetById(atomId)!.Name;
            if (name == "user$go")
            {
                sawGo = true;
                Assert.True(e.IsDebuggableFunctor(pred.FunctorId),
                    "the program's own go/1 is not debuggable: nothing here is measured");
            }
            else if (name.StartsWith("dbglib$", StringComparison.Ordinal))
            {
                library.Add($"{name}/{arity}");
                Assert.False(e.IsDebuggableFunctor(pred.FunctorId),
                    $"a debugger stops in the library's {name}/{arity}");
            }
        }
        Assert.True(sawGo, "go/1 is not in the link");
        Assert.Contains("dbglib$twice/2", library);
        Assert.Contains("dbglib$dbl/2", library);
    }
}

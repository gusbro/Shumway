using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The clauses of a dynamic or multifile predicate link and run like
/// static ones. The object's call graph for them is read from the clauses as
/// written, and through the consult pipeline from a multifile clause already
/// rewritten under its module, where a call to the local <c>q/1</c> reads
/// <c>m$q</c> and one to an import <c>Source$name</c>: the linker reported
/// those undefined, and the <c>V^</c> of a setof goal as a call to
/// <c>^/2</c>.</summary>
public sealed class DynamicClauseLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "shumway-dynlink-" + System.Guid.NewGuid().ToString("N"));

    public DynamicClauseLinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static PrologEngine LinkAndLoad(IEnumerable<ShmoObject> objects,
        PredicateRef entry, params string[] libraryDirs)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects.ToArray(),
            EntryPoints = new[] { entry },
            LibraryDirs = libraryDirs,
        });
        Assert.True(link.Success,
            string.Join("\n", link.Diagnostics.Select(d => d.Message)));
        Assert.Empty(link.MissingPredicates);
        var e = new PrologEngine();
        e.LoadBundle(link.Bundle!);
        return e;
    }

    [Fact]
    public void AMultifileClauseLinksItsCallsToLocals()
    {
        // A module-less root is compiled as a module named after its file.
        // '$p#1' is how Logtalk names an entity's predicate.
        string root = Path.Combine(_dir, "m.pl");
        File.WriteAllText(root, @"
:- public(main/1).
:- multifile(hook/1).
hook(X) :- '$p#1'(X).
hook(X) :- q(X).
'$p#1'(a).
q(b).
main(L) :- findall(X, hook(X), L).
");
        var errors = new List<ShmoCompileError>();
        var objects = ShmoViaConsult.Compile(
            root, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);

        var e = LinkAndLoad(objects.Select(o => o.Object), new PredicateRef("main", 1));
        Assert.True(e.Query("main(L), L == [a,b].").Success);
    }

    [Fact]
    public void AConsultedMultifileClauseLinksItsImportsAndMetaGoals()
    {
        File.WriteAllText(Path.Combine(_dir, "rlib.pl"), @"
:- module(rlib, [r/1]).
r(c).
");
        string root = Path.Combine(_dir, "k.pl");
        File.WriteAllText(root, @"
:- module(k, [main/1]).
:- use_module(library(rlib)).
:- multifile(hook/1).
hook(imp(L)) :- findall(X, r(X), L).
hook(set(S)) :- setof(Y, Z^w(Y, Z), S).
hook(clo(L)) :- findall(Y, call(w, Y, _), L).
w(1, a). w(2, b).
main(Hs) :- findall(H, hook(H), Hs).
");
        var errors = new List<ShmoCompileError>();
        var objects = ShmoViaConsult.Compile(
            root, new[] { _dir }, ShmoBuildMode.Release, errors);
        Assert.Empty(errors);

        var e = LinkAndLoad(objects.Select(o => o.Object),
            new PredicateRef("main", 1), _dir);
        Assert.True(e.Query(
            "main(Hs), Hs == [imp([c]), set([1, 2]), clo([1, 2])].").Success);
    }

    [Fact]
    public void FileAtATimeDynamicClausesLinkTheirMetaGoals()
    {
        var obj = ShmoCompiler.CompileSource(@"
:- module(fm, [main/1]).
:- multifile(hook/1).
:- dynamic(dyn/1).
hook(all(L)) :- findall(X, q(X), L).
hook(clo(L)) :- findall(Y, call(w, Y, _), L).
dyn(S) :- setof(Y, Z^w(Y, Z), S).
q(b).
w(1, a). w(2, b).
main(Hs-S) :- findall(H, hook(H), Hs), dyn(S).
", "fm");

        var e = LinkAndLoad(new[] { obj }, new PredicateRef("main", 1));
        Assert.True(e.Query(
            "main(R), R == [all([b]), clo([1, 2])]-[1, 2].").Success);
    }
}

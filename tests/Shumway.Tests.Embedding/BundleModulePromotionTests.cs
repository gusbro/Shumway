using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>REPL usability (ADR-038, version a): loading a `.shum` leaves the
/// top level standing in `user`, so a bundle's module-local predicates are
/// invisible by bare name — unlike consulting the equivalent source.
/// <see cref="PrologEngine.PromoteSingleBareBundleModuleToUser"/> aliases a
/// single bare (non-library) module's locals into `user`. A library module
/// (`:- module(Name,[Exports])`) is never promoted.</summary>
public sealed class BundleModulePromotionTests
{
    private sealed class TempDir : System.IDisposable
    {
        public string Dir { get; }
        public TempDir()
        {
            Dir = Path.Combine(Path.GetTempPath(),
                "shumway-promote-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }
        public string Add(string name, string source)
        {
            string p = Path.Combine(Dir, name);
            File.WriteAllText(p, source);
            return p;
        }
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // Build a source-stripped (Release) bundle from a single root file via the
    // consult pipeline, so the loaded entry takes the bytecode path that
    // populates the precompiled-locals table the promotion reads.
    private static Bundle BundleFrom(string rootPath, params PredicateRef[] entries)
    {
        var errors = new System.Collections.Generic.List<ShmoCompileError>();
        var objects = ShmoViaConsult.Compile(
            rootPath, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects.Select(o => o.Object).ToArray(),
            EntryPoints = entries,
            AllowUndefined = true,
        });
        Assert.True(link.Success);
        return link.Bundle!;
    }

    [Fact]
    public void BareModuleLocals_PromotedToUser_SoTopLevelMetaCallResolves()
    {
        // A module-less file: the consult pipeline names it after the file
        // (`prog`), so its predicates become module-locals (prog$pick, …). Only
        // `run/0` is the entry (linker promotes an entry local to public), so
        // pick/1 and step/2 stay genuine non-entry locals.
        using var t = new TempDir();
        string root = t.Add("prog.pl",
            "run :- pick(_).\n" +
            "pick(X) :- step(X, 0).\n" +
            "step(done, _).\n");

        var bundle = BundleFrom(root, new PredicateRef("run", 0));

        // Before promotion: a bare meta-call from `user` cannot see a non-entry
        // local — it raises existence_error (throws) or fails; not visible.
        var pre = new PrologEngine();
        pre.LoadBundle(bundle);
        Assert.False(TrySucceeds(pre, "once(pick(_))."));

        // Promote, then the same meta-call resolves.
        var e = new PrologEngine();
        e.LoadBundle(bundle);
        var promoted = e.PromoteSingleBareBundleModuleToUser();
        Assert.NotNull(promoted);
        Assert.Equal("prog", promoted!.Value.Module);
        var names = promoted.Value.Predicates.Select(p => $"{p.Name}/{p.Arity}").ToList();
        Assert.Contains("pick/1", names);
        Assert.Contains("step/2", names);

        Assert.True(e.Query("once(pick(X)), X == done.").Success);
        Assert.True(e.Query("findall(X, step(X,_), [done]).").Success);
    }

    [Fact]
    public void LibraryModule_IsNeverPromoted()
    {
        // An export-qualified module — its names are deliberately namespaced.
        using var t = new TempDir();
        string root = t.Add("mylib.pl",
            ":- module(mylib, [api/1]).\n" +
            "api(X) :- secret(X).\n" +
            "secret(42).\n");

        var bundle = BundleFrom(root, new PredicateRef("api", 1));
        var e = new PrologEngine();
        e.LoadBundle(bundle);

        // No bare module to promote → null, and the library's internal
        // `secret/1` stays invisible at the top level.
        Assert.Null(e.PromoteSingleBareBundleModuleToUser());
        Assert.False(TrySucceeds(e, "once(secret(_))."));
    }

    // A top-level meta-call of an undefined name raises existence_error (default
    // unknown=error). "Not visible" means it does not succeed — throw or fail.
    private static bool TrySucceeds(PrologEngine e, string goal)
    {
        try { return e.Query(goal).Success; }
        catch (System.Exception) { return false; }
    }
}

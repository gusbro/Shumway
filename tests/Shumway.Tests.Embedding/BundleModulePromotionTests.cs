using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>REPL usability (ADR-038): loading a `.shum` leaves the top level
/// standing in `user`, so a bundle's module-local predicates are invisible by
/// bare name — unlike consulting the equivalent source.
/// <see cref="PrologEngine.PromoteBareBundleModulesToUser"/> aliases each bare
/// (non-library) module's locals into `user`, inherits its imports (so raw
/// library goals resolve at the prompt), and skips — wholesale — any module
/// whose name would collide. Libraries (`:- module(Name,[Exports])`) are never
/// promoted.</summary>
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

    // Source-stripped (Release) bundle from a root file via the consult
    // pipeline, so the loaded entry takes the bytecode path that populates the
    // precompiled-locals table the promotion reads.
    private static Bundle ViaConsultBundle(string rootPath, params PredicateRef[] entries)
    {
        var errors = new System.Collections.Generic.List<ShmoCompileError>();
        var objects = ShmoViaConsult.Compile(
            rootPath, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        return LinkObjects(objects.Select(o => o.Object), entries);
    }

    // Source-stripped bundle from several file-at-a-time compiled objects —
    // used to place several distinct bare modules into one bundle.
    private static Bundle FileAtATimeBundle(
        System.Collections.Generic.IEnumerable<string> paths, params PredicateRef[] entries)
    {
        var objs = new System.Collections.Generic.List<ShmoObject>();
        foreach (string p in paths)
        {
            var r = Shumway.Embedding.ShmoCompiler.TryCompileFile(
                p, ShmoBuildMode.Release, maxErrors: 100);
            Assert.True(r.Success);
            objs.Add(r.Object!);
        }
        return LinkObjects(objs, entries);
    }

    private static Bundle LinkObjects(
        System.Collections.Generic.IEnumerable<ShmoObject> objs, PredicateRef[] entries)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objs.ToArray(),
            EntryPoints = entries,
            AllowUndefined = true,
        });
        Assert.True(link.Success);
        return link.Bundle!;
    }

    // A top-level meta-call of an undefined name raises existence_error (default
    // unknown=error). "Not visible" means it does not succeed — throw or fail.
    private static bool Succeeds(PrologEngine e, string goal)
    {
        try { return e.Query(goal).Success; }
        catch (System.Exception) { return false; }
    }

    [Fact]
    public void BundleLoad_IsLazy_AndCompileAllWarmsTheSet()
    {
        // A t0 bundle (no persisted IL) must not eagerly Sigil-compile its
        // predicates at load; compile_all front-loads them on demand.
        using var t = new TempDir();
        string root = t.Add("arith.pl",
            ":- public add/3.\n" +
            "add(0, Y, Y).\n" +
            "add(s(X), Y, s(Z)) :- add(X, Y, Z).\n");
        var bundle = ViaConsultBundle(root, new PredicateRef("add", 3));

        var e = new PrologEngine();
        e.IlPromotion.Threshold = 32;                 // enable Tier-1
        int before = e.IlPromotion.PromotedCount;
        e.LoadBundle(bundle);
        // Lazy: load compiled nothing (no persisted IL, no eager warm).
        Assert.Equal(before, e.IlPromotion.PromotedCount);

        int n = e.WarmAllCompilable();
        Assert.True(n >= 1, "compile_all should promote at least one predicate");
        Assert.True(e.IlPromotion.PromotedCount > before);
        // Still correct after warming.
        Assert.True(e.Query("add(s(0), s(0), R), R == s(s(0)).").Success);
    }

    [Fact]
    public void CompileAll1_Builtin_UnifiesTheCount()
    {
        using var t = new TempDir();
        string root = t.Add("arith.pl",
            ":- public add/3.\n" +
            "add(0, Y, Y).\n" +
            "add(s(X), Y, s(Z)) :- add(X, Y, Z).\n");
        var bundle = ViaConsultBundle(root, new PredicateRef("add", 3));

        var e = new PrologEngine();
        e.IlPromotion.Threshold = 32;
        e.LoadBundle(bundle);
        var sol = e.Query("compile_all(N).");
        Assert.True(sol.Success);
        Assert.True(sol.Get<int>("N") >= 1);
    }

    [Fact]
    public void BareModuleLocals_PromotedToUser_SoTopLevelMetaCallResolves()
    {
        // A module-less file: the consult pipeline names it after the file
        // (`prog`), so its predicates become module-locals. Only `run/0` is the
        // entry (linker promotes an entry local to public); pick/1 and step/2
        // stay genuine non-entry locals.
        using var t = new TempDir();
        string root = t.Add("prog.pl",
            "run :- pick(_).\n" +
            "pick(X) :- step(X, 0).\n" +
            "step(done, _).\n");
        var bundle = ViaConsultBundle(root, new PredicateRef("run", 0));

        // Before promotion: a bare meta-call from `user` cannot see a non-entry
        // local — existence_error (throws) or fails; not visible either way.
        var pre = new PrologEngine();
        pre.LoadBundle(bundle);
        Assert.False(Succeeds(pre, "once(pick(_))."));

        var e = new PrologEngine();
        e.LoadBundle(bundle);
        var outcome = e.PromoteBareBundleModulesToUser();
        var prog = Assert.Single(outcome.Promoted, p => p.Module == "prog");
        var names = prog.Predicates.Select(p => $"{p.Name}/{p.Arity}").ToList();
        Assert.Contains("pick/1", names);
        Assert.Contains("step/2", names);
        Assert.Empty(outcome.SkippedForCollision);

        Assert.True(e.Query("once(pick(X)), X == done.").Success);
        Assert.True(e.Query("findall(X, step(X,_), [done]).").Success);
    }

    [Fact]
    public void PromotedModule_InheritsItsImports_SoRawLibraryGoalsResolve()
    {
        // The bare program imports op_add/3 from a library; standing in it means
        // a raw op_add(...) at the prompt must resolve, not just prog's own preds.
        using var t = new TempDir();
        t.Add("adder.pl",
            ":- module(adder, [op_add/3]).\n" +
            "op_add(A, B, C) :- C is A + B.\n");
        // calc/1 is prog's own local (so prog is a promotion candidate — a
        // module you can "stand in"); run/0 is the entry.
        string root = t.Add("prog.pl",
            ":- use_module(library(adder)).\n" +
            "run :- calc(_).\n" +
            "calc(R) :- op_add(1, 2, R).\n");
        var bundle = ViaConsultBundle(root, new PredicateRef("run", 0));

        var e = new PrologEngine();
        e.LoadBundle(bundle);
        // adder is a library — never promoted; op_add invisible before promote.
        Assert.False(Succeeds(e, "op_add(1, 2, _)."));

        var outcome = e.PromoteBareBundleModulesToUser();
        Assert.Contains(outcome.Promoted, p => p.Module == "prog");
        // `user` inherited prog's import of op_add → the raw goal resolves, and
        // prog's own local is callable too.
        Assert.True(e.Query("op_add(1, 2, R), R == 3.").Success);
        Assert.True(e.Query("calc(R), R == 3.").Success);
    }

    [Fact]
    public void CollidingBareModules_AreBothSkippedWholesale_CleanOnePromoted()
    {
        // a and b both define a local dup/1 → the name collides in `user`; c's
        // uniq/1 is unique. All-or-nothing: a AND b are skipped entirely, c is
        // promoted. (1-arg :- module makes each a named, non-export-qualified
        // bare module with local predicates.)
        using var t = new TempDir();
        string a = t.Add("a.pl", ":- module(a).\ndup(1).\ngo_a :- dup(_).\n");
        string b = t.Add("b.pl", ":- module(b).\ndup(2).\ngo_b :- dup(_).\n");
        string c = t.Add("c.pl", ":- module(c).\nuniq(9).\ngo_c :- uniq(_).\n");
        var bundle = FileAtATimeBundle(new[] { a, b, c },
            new PredicateRef("go_a", 0), new PredicateRef("go_b", 0),
            new PredicateRef("go_c", 0));

        var e = new PrologEngine();
        e.LoadBundle(bundle);
        var outcome = e.PromoteBareBundleModulesToUser();

        var skippedModules = outcome.SkippedForCollision.Select(s => s.Module).ToHashSet();
        Assert.Contains("a", skippedModules);
        Assert.Contains("b", skippedModules);
        Assert.Contains(outcome.Promoted, p => p.Module == "c");
        // Every skip names the offending indicator.
        foreach (var s in outcome.SkippedForCollision)
            Assert.Contains(s.Predicates, p => p.Name == "dup" && p.Arity == 1);

        // c's local resolves at the top level; the contested dup/1 does not.
        Assert.True(e.Query("once(uniq(X)), X == 9.").Success);
        Assert.False(Succeeds(e, "once(dup(_))."));
    }

    [Fact]
    public void LibraryModule_IsNeverPromoted()
    {
        using var t = new TempDir();
        string root = t.Add("mylib.pl",
            ":- module(mylib, [api/1]).\n" +
            "api(X) :- secret(X).\n" +
            "secret(42).\n");
        var bundle = ViaConsultBundle(root, new PredicateRef("api", 1));

        var e = new PrologEngine();
        e.LoadBundle(bundle);
        var outcome = e.PromoteBareBundleModulesToUser();
        Assert.Empty(outcome.Promoted);
        Assert.Empty(outcome.SkippedForCollision);
        Assert.False(Succeeds(e, "once(secret(_))."));
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <see cref="PrologEngine.ModuleDefinesFunctor"/> answers without walking the
/// module: it must say what the enumeration it stands for says, for every
/// kind of module and every kind of predicate.
/// </summary>
public sealed class ModuleDefinesFunctorTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "shumway-defines-" + System.Guid.NewGuid().ToString("N"));

    public ModuleDefinesFunctorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PrologEngine EngineWithEveryKindOfModule()
    {
        // A bare file linked into a bundle: a module named after the file,
        // with locals, a public predicate and a dynamic one.
        string bare = Path.Combine(_dir, "barebundle.pl");
        File.WriteAllText(bare,
            ":- public shown/1.\n:- dynamic counted/1.\n"
            + "shown(X) :- helper(X).\nhelper(1).\ncounted(0).\n'$hidden'(1).\n");
        var errors = new List<ShmoCompileError>();
        var objects = ShmoViaConsult.Compile(bare, System.Array.Empty<string>(),
            ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects.Select(o => o.Object).ToArray(),
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics));

        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!)));
        // A module under :- module/2, owning a dynamic it declares and one it
        // is asserted into.
        e.ConsultString(":- module(qual, [api/1]).\n:- dynamic store/1.\n"
            + "api(X) :- inner(X).\ninner(2).\nstore(a).\n");
        // A dynamic declared by user and then again by a module: the record
        // names the first.
        e.ConsultString(":- dynamic shared_db/1.\n");
        // A module under :- module/1.
        e.ConsultString(":- module(arity).\nold(1).\n:- dynamic old_db/1.\nold_db(x).\n"
            + ":- dynamic shared_db/1.\n");
        // user: consulted clauses, a dynamic, an asserted one.
        e.ConsultString("top(1).\n:- dynamic top_db/1.\n");
        Assert.True(e.Query("assertz(top_db(1)), assertz(fresh_fact(2)), qual:assertz(added(3)).").Success);
        return e;
    }

    [Fact]
    public void ItSaysWhatTheEnumerationSays()
    {
        var e = EngineWithEveryKindOfModule();
        var modules = e.VisibleModuleNames();
        Assert.Contains("barebundle", modules);
        Assert.Contains("qual", modules);
        Assert.Contains("arity", modules);

        // Every functor any module defines, the engine's own names, builtins
        // and names nobody defines.
        var candidates = new HashSet<int>(e.DefinedModulePredicates(null).Select(p => p.Fid));
        foreach (string text in new[] { "$hidden/1", "helper/1", "inner/1", "store/1", "added/1",
                                        "atom_length/2", "append/3", "nowhere/0", "counted/1", "shared_db/1" })
        {
            int slash = text.LastIndexOf('/');
            candidates.Add(FunctorTable.Intern(
                AtomTable.Intern(text[..slash], permanent: true).Id, int.Parse(text[(slash + 1)..])));
        }
        Assert.True(candidates.Count > 15, $"only {candidates.Count} functors to ask about");

        var disagreements = new List<string>();
        int defined = 0;
        foreach (string module in modules)
        {
            var enumerated = new HashSet<int>(e.DefinedModulePredicates(module).Select(p => p.Fid));
            foreach (int fid in candidates)
            {
                bool expected = enumerated.Contains(fid) || e.ModuleOwnsDynamicFunctor(module, fid);
                bool answered = e.ModuleDefinesFunctor(module, fid);
                if (answered) defined++;
                if (answered != expected)
                {
                    var (atomId, arity) = FunctorTable.Lookup(fid);
                    disagreements.Add($"{module}: {AtomTable.GetById(atomId)?.Name}/{arity} "
                        + $"enumerated {expected}, answered {answered}");
                }
            }
        }
        Assert.True(defined >= 10, $"only {defined} definitions were found");
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }
}

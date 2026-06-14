using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Linker library inputs (C-archive semantics): <c>.shum</c>
/// librarian archives whose members are pulled in only on demand, FIFO, to
/// satisfy references the explicit <c>.shmo</c> objects leave unresolved.</summary>
public sealed class LinkLibraryTests
{
    private static ShmoObject Obj(string src, string module)
        => ShmoCompiler.CompileSource(src, module);

    private static LinkLibrary Lib(string name, params ShmoObject[] members)
        => new(name, members);

    private static LinkResult Link(
        ShmoObject[] objects, LinkLibrary[] libraries, params (string, int)[] entries)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            Libraries = libraries,
            EntryPoints = entries.Select(e => new PredicateRef(e.Item1, e.Item2)).ToArray(),
        });

    private const string Greet =
        ":- module(greet).\n:- public hello/1.\nhello(N) :- write(hello(N)), nl.\n";
    private const string Unused =
        ":- module(unused).\n:- public dead/0.\ndead.\n";
    private const string App =
        ":- module(app).\n:- public run/0.\nrun :- hello(world).\n";

    [Fact]
    public void PullsNeededMember_LeavesUnusedUnlinked()
    {
        var lib = Lib("lib.shum", Obj(Greet, "greet"), Obj(Unused, "unused"));
        var r = Link(new[] { Obj(App, "app") }, new[] { lib }, ("run", 0));

        Assert.True(r.Success);
        Assert.Contains("greet", r.ReachedModules);     // pulled to satisfy hello/1
        Assert.DoesNotContain("unused", r.ReachedModules); // nothing needed it
        Assert.DoesNotContain("unused", r.UnreachableModules); // never even an input

        var engine = new PrologEngine();
        engine.LoadBundle(r.Bundle!);
        Assert.True(engine.Query("run.").Success);
    }

    [Fact]
    public void Fifo_FirstLibraryProvidingTheSymbolWins()
    {
        // Two libraries both export greeting/1 (in different modules).
        var v1 = Lib("v1.shum",
            Obj(":- module(v1).\n:- public greeting/1.\ngreeting(_).\n", "v1"));
        var v2 = Lib("v2.shum",
            Obj(":- module(v2).\n:- public greeting/1.\ngreeting(_).\n", "v2"));
        var app = Obj(":- module(g).\n:- public go/0.\ngo :- greeting(x).\n", "g");

        var first = Link(new[] { app }, new[] { v1, v2 }, ("go", 0));
        Assert.Contains("v1", first.ReachedModules);
        Assert.DoesNotContain("v2", first.ReachedModules);

        var swapped = Link(new[] { app }, new[] { v2, v1 }, ("go", 0));
        Assert.Contains("v2", swapped.ReachedModules);
        Assert.DoesNotContain("v1", swapped.ReachedModules);
    }

    [Fact]
    public void PullsTransitively_AcrossLibraries()
    {
        var liba = Lib("a.shum",
            Obj(":- module(a).\n:- public f/0.\nf :- g.\n", "a"));
        var libb = Lib("b.shum",
            Obj(":- module(b).\n:- public g/0.\ng :- write(done), nl.\n", "b"));
        var app = Obj(":- module(app2).\n:- public run2/0.\nrun2 :- f.\n", "app2");

        var r = Link(new[] { app }, new[] { liba, libb }, ("run2", 0));
        Assert.True(r.Success);
        Assert.Contains("a", r.ReachedModules);
        Assert.Contains("b", r.ReachedModules);

        var engine = new PrologEngine();
        engine.LoadBundle(r.Bundle!);
        Assert.True(engine.Query("run2.").Success);
    }

    [Fact]
    public void MissingPredicate_NoProvider_StillErrors()
    {
        var bad = Obj(
            ":- module(bad).\n:- public b/0.\nb :- totally_undefined(x).\n", "bad");
        var lib = Lib("lib.shum", Obj(Greet, "greet")); // does not provide it
        var r = Link(new[] { bad }, new[] { lib }, ("b", 0));

        Assert.False(r.Success);
        Assert.Contains(new PredicateRef("totally_undefined", 1), r.MissingPredicates);
    }

    [Fact]
    public void ExplicitObject_WinsOverLibrary()
    {
        // hello/1 is provided BOTH by an explicit object (module ex) and by a
        // library member (module libg). The explicit one satisfies the call,
        // so the library member is never pulled — and there is no
        // duplicate_public, because only one hello/1 enters the link.
        var explicitHello = Obj(
            ":- module(ex).\n:- public hello/1.\nhello(_) :- write(from_ex), nl.\n", "ex");
        var lib = Lib("lib.shum",
            Obj(":- module(libg).\n:- public hello/1.\nhello(_) :- write(from_lib), nl.\n", "libg"));
        var app = Obj(App, "app");

        var r = Link(new[] { app, explicitHello }, new[] { lib }, ("run", 0));
        Assert.True(r.Success);
        Assert.Contains("ex", r.ReachedModules);
        Assert.DoesNotContain("libg", r.ReachedModules);
    }

    [Fact]
    public void LinkedObjects_IncludePulledMembers_ForTheMap()
    {
        // result.LinkedObjects = explicit objects + pulled members (what the
        // --map writer consumes), so a pulled module is visible there.
        var lib = Lib("lib.shum", Obj(Greet, "greet"), Obj(Unused, "unused"));
        var r = Link(new[] { Obj(App, "app") }, new[] { lib }, ("run", 0));

        var linkedModules = r.LinkedObjects.Select(o => o.ModuleName).ToHashSet();
        Assert.Contains("app", linkedModules);
        Assert.Contains("greet", linkedModules);     // pulled — now in LinkedObjects
        Assert.DoesNotContain("unused", linkedModules); // not pulled
    }

    [Fact]
    public void ForeignPredicate_IsNotPulledFromALibrary()
    {
        // A library member exports c247_double/2 — the SAME indicator a foreign
        // DLL (C247Math) provides. With the foreign assembly given, the linker
        // resolves the call to the foreign and must NOT pull the library member
        // (a foreign is "already available", like a builtin).
        string testDll = typeof(C247Math).Assembly.Location;
        var app = Obj(
            ":- module(fapp).\n:- public run/0.\nrun :- c247_double(5, X), X = 10.\n", "fapp");
        var lib = Lib("lib.shum",
            Obj(":- module(libdbl).\n:- public c247_double/2.\nc247_double(_, _).\n", "libdbl"));

        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { app },
            Libraries = new[] { lib },
            EntryPoints = new[] { new PredicateRef("run", 0) },
            ForeignAssemblies = new[] { testDll },
        });

        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("libdbl", r.ReachedModules);  // foreign won; nothing pulled
    }

    [Fact]
    public void EntryPointDefinedOnlyInLibrary_IsPulled()
    {
        // No explicit objects at all: the entry point and its callee both
        // live in libraries and are pulled in from the roots.
        var libApp = Lib("app.shum", Obj(App, "app"));
        var libGreet = Lib("greet.shum", Obj(Greet, "greet"));

        var r = Link(System.Array.Empty<ShmoObject>(),
            new[] { libApp, libGreet }, ("run", 0));
        Assert.True(r.Success);
        Assert.Contains("app", r.ReachedModules);
        Assert.Contains("greet", r.ReachedModules);

        var engine = new PrologEngine();
        engine.LoadBundle(r.Bundle!);
        Assert.True(engine.Query("run.").Success);
    }
}

using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What <c>current_predicate/1</c> and <c>predicate_property/2</c>
/// say about the predicates of a <c>:- module/2</c> file (ADR-038). The
/// global namespace, which is <c>user</c>'s, has none of them: an importer
/// sees an export through <c>imported_from/1</c>, and nobody outside sees a
/// private. Asked with no module from inside such a file, the file is asked.
/// The answers are the ones measured on the reference system for modules.
/// Text without <c>:- module/2</c> sees what <c>user</c> imports, however it
/// is loaded.</summary>
public sealed class ModuleReflectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_mr_" + Guid.NewGuid().ToString("N"));

    private const string Dep = @"
:- module(mr_dep, [dpub/1]).
dpub(a).
dpriv(b).
";

    // Asks from inside the module, with no qualifier.
    private const string Inside = @"
:- module(mr_in, [in_pub/0, inside/1, all_inside/1]).
:- use_module('mr_dep.pl').
in_pub.
in_priv.
:- dynamic(in_dyn/1).
inside(PI) :- current_predicate(PI).
all_inside(L) :- findall(N/A, current_predicate(N/A), L0), sort(L0, L).
";

    private const string Program = @"
:- use_module('mr_dep.pl').
mine(1).
:- dynamic(dyn/1).
";

    // Text without :- module/2 asking about what user can call, as Logtalk's
    // runtime asks whether a predicate user imported exists.
    private const string Bare = @"
bare_property(H, P) :- predicate_property(H, P).
bare_clause(H, B) :- clause(H, B).
";

    // The same questions under :- module/2, which does not import mr_dep.
    private const string Scoped = @"
:- module(mr_scoped, [scoped_property/2, scoped_clause/2]).
scoped_property(H, P) :- predicate_property(H, P).
scoped_clause(H, B) :- clause(H, B).
";

    public ModuleReflectionTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mr_dep.pl"), Dep);
        File.WriteAllText(Path.Combine(_dir, "mr_in.pl"), Inside);
        File.WriteAllText(Path.Combine(_dir, "mr_user.pl"), Program);
        File.WriteAllText(Path.Combine(_dir, "mr_bare.pl"), Bare);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PrologEngine Loaded(string? insideDialect = null)
    {
        var e = new PrologEngine();
        e.ConsultFile(Path.Combine(_dir, "mr_user.pl"));
        string inside = Path.Combine(_dir, "mr_in.pl");
        if (insideDialect is null) e.ConsultFile(inside);
        else e.WithLibraryDialect(insideDialect, () => { e.ConsultFile(inside); return true; });
        return e;
    }

    private static void Holds(PrologEngine e, string goal) => Assert.True(e.Query(goal).Success, goal);
    private static void Fails(PrologEngine e, string goal) => Assert.False(e.Query(goal).Success, goal);

    [Fact]
    public void User_DefinesWhatItsFilesDefine_AndNothingOfAModule()
    {
        var e = Loaded();

        Holds(e, @"findall(N/A, ( current_predicate(user:N/A), memberchk(N, [mine, dyn, dpub, dpriv, in_pub, in_priv]) ), L0),
                   sort(L0, L), L == [dyn/1, mine/1].");
        Holds(e, @"findall(N/A, ( current_predicate(N/A), memberchk(N, [mine, dyn, dpub, dpriv, in_pub, in_priv]) ), L0),
                   sort(L0, L), L == [dyn/1, mine/1].");
        Fails(e, @"current_predicate(user:dpriv/1).");
        Fails(e, @"current_predicate(dpriv/1).");
        Fails(e, @"current_predicate(user:dpub/1).");
        // The module still has both, and is the only one that does.
        Holds(e, @"findall(N/A, current_predicate(mr_dep:N/A), L0), sort(L0, L), L == [dpriv/1, dpub/1].");
        Holds(e, @"findall(M, current_predicate(M:dpub/1), L), L == [mr_dep].");
        Holds(e, @"findall(M, current_predicate(M:dpriv/1), L), L == [mr_dep].");
    }

    [Fact]
    public void AskedWithNoModule_AProgramsOwnDollarNameIsStillFound()
    {
        var e = Loaded();
        e.ConsultString("'$mr_own'(1).");

        // A program that names its predicates this way asks for them this way.
        Holds(e, @"current_predicate('$mr_own'/1).");
        Holds(e, @"findall(N, ( current_predicate(N/1), sub_atom(N, 0, 4, _, '$mr_') ), L), L == ['$mr_own'].");
    }

    [Fact]
    public void Properties_SayWhereAPredicateIsFrom()
    {
        var e = Loaded();

        Holds(e, @"findall(P, predicate_property(user:dpub(_), P), L), L == [static, defined, imported_from(mr_dep)].");
        Holds(e, @"findall(P, predicate_property(dpub(_), P), L), L == [static, defined, imported_from(mr_dep)].");
        Holds(e, @"findall(P, predicate_property(mr_dep:dpub(_), P), L), L == [static, defined, exported].");
        Holds(e, @"findall(P, predicate_property(mr_dep:dpriv(_), P), L), L == [static, defined].");
        Holds(e, @"findall(P, predicate_property(user:mine(_), P), L), L == [static, defined].");
        // A private is nothing to who cannot call it.
        Fails(e, @"predicate_property(user:dpriv(_), _).");
        Fails(e, @"predicate_property(dpriv(_), _).");
        Fails(e, @"predicate_property(mr_in:dpriv(_), _).");
        Holds(e, @"catch(dpriv(_), error(existence_error(procedure, dpriv/1), _), true).");
    }

    [Fact]
    public void InsideAModule_AskedWithNoModule_TheModuleIsAsked()
    {
        var e = Loaded();

        Holds(e, @"inside(in_priv/0), inside(in_pub/0), inside(in_dyn/1).");
        Fails(e, @"inside(dpub/1).");           // an import is not a definition
        Fails(e, @"inside(dpriv/1).");
        Fails(e, @"inside(mine/1).");
        Fails(e, @"inside(dyn/1).");
        Fails(e, @"inside(atom_length/2).");
        Holds(e, @"all_inside(L), L == [all_inside/1, in_dyn/1, in_priv/0, in_pub/0, inside/1].");
        // The import shows where it is asked about.
        Holds(e, @"predicate_property(mr_in:dpub(_), imported_from(mr_dep)).");
    }

    /// <summary>The bare text loaded the four ways it can be: consulted into
    /// user, under <c>:- module/1</c>, compiled on its own and linked, and
    /// linked through a consult. The last two give it the file's name as its
    /// module, as an executable with Logtalk embedded gets.</summary>
    private PrologEngine LoadedBare(string how)
    {
        var e = new PrologEngine();
        switch (how)
        {
            case "consulted":
                e.ConsultString(Bare);
                break;
            case "module1":
                e.ConsultString(":- module(mr_bare).\n:- public bare_property/2, bare_clause/2.\n" + Bare);
                break;
            case "compiled":
            {
                var r = ShmoCompiler.TryCompileSource(Bare, "mr_bare");
                Assert.True(r.Success, string.Join("; ", r.Errors));
                LoadLinked(e, new[] { ShmoReader.FromBytes(ShmoWriter.ToBytes(r.Object!)) });
                break;
            }
            case "consult-linked":
            {
                var errors = new System.Collections.Generic.List<ShmoCompileError>();
                var objects = ShmoViaConsult.Compile(Path.Combine(_dir, "mr_bare.pl"),
                    Array.Empty<string>(), ShmoBuildMode.Release, errors);
                Assert.Empty(errors);
                Assert.Contains(objects, o => o.ModuleName == "mr_bare");
                LoadLinked(e, System.Linq.Enumerable.ToArray(
                    System.Linq.Enumerable.Select(objects, o => o.Object)));
                break;
            }
        }
        // What user imports after the text is in, as the application's loader
        // does in the executable.
        e.ConsultFile(Path.Combine(_dir, "mr_user.pl"));
        return e;
    }

    private static void LoadLinked(PrologEngine e, ShmoObject[] objects)
    {
        var link = ShmoLinker.Link(new LinkConfig { Objects = objects, Library = true, AllowUndefined = true });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics));
        e.LoadBundle(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!)));
        e.PromoteBareBundleModulesToUser();
    }

    [Theory]
    [InlineData("consulted")]
    [InlineData("module1")]
    [InlineData("compiled")]
    [InlineData("consult-linked")]
    public void TextWithoutModule2_SeesWhatUserImports_HoweverItIsLoaded(string how)
    {
        var e = LoadedBare(how);

        Holds(e, @"bare_property(dpub(_), imported_from(mr_dep)).");
        Holds(e, @"bare_property(dpub(_), defined).");
        Holds(e, @"findall(X, bare_clause(dpub(X), true), L), L == [a].");
        // What user cannot call, it does not see either.
        Fails(e, @"bare_property(dpriv(_), _).");
    }

    [Fact]
    public void TextUnderModule2_DoesNotSeeWhatUserImports()
    {
        var e = Loaded();
        e.ConsultString(Scoped);

        Holds(e, @"predicate_property(dpub(_), imported_from(mr_dep)).");
        Fails(e, @"mr_scoped:scoped_property(dpub(_), _).");
        Fails(e, @"mr_scoped:scoped_clause(dpub(_), _).");
    }

    [Fact]
    public void InsideAModuleOfSwiCode_AskedWithNoModule_WhatItCanCallIsSeen()
    {
        var e = Loaded(insideDialect: "swi");

        Holds(e, @"inside(in_priv/0), inside(in_pub/0), inside(in_dyn/1).");
        Holds(e, @"inside(dpub/1).");
        Holds(e, @"inside(mine/1).");
        Holds(e, @"inside(dyn/1).");
        Holds(e, @"inside(atom_length/2).");
        // Another module's private is still not its to see.
        Fails(e, @"inside(dpriv/1).");
    }
}

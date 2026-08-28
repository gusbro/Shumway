using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>shumway-compile --consult: compilation THROUGH the consult
/// pipeline (ShmoViaConsult) so load-time computation runs — in-file
/// term_expansion generating clauses, operators defined by a dependency,
/// sibling-directory library resolution — and every module in the chain
/// serialises to its own linkable ShmoObject.</summary>
public sealed class ViaConsultCompileTests
{
    private sealed class TempDir : System.IDisposable
    {
        public string Dir { get; }
        public TempDir()
        {
            Dir = Path.Combine(Path.GetTempPath(),
                "shumway-viaconsult-" + System.Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void TermExpansionGeneratedClauses_EndUpInTheObject()
    {
        // The clpz shape: a marker term expands — by EXECUTING a predicate
        // defined earlier in the same file — into generated clauses. A
        // file-at-a-time compile cannot produce gen_fact/1; via-consult can.
        using var t = new TempDir();
        string root = t.Add("genmod.pl",
            "make_facts([gen_fact(1), gen_fact(2), gen_fact(3)]).\n" +
            "term_expansion(make_facts_marker, Clauses) :- make_facts(Clauses).\n" +
            "make_facts_marker.\n" +
            "sum_facts(S) :- gen_fact(A), gen_fact(B), A < B, S is A + B.\n");
        var errors = new System.Collections.Generic.List<ShmoCompileError>();
        var objects = ShmoViaConsult.Compile(
            root, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        var rootObj = Assert.Single(objects, o => o.ModuleName == "genmod").Object;

        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { rootObj },
            EntryPoints = new[] { new PredicateRef("sum_facts", 1) },
        });
        Assert.True(link.Success);
        var e = new PrologEngine();
        e.LoadBundle(link.Bundle!);
        var sols = e.QueryAll("sum_facts(S).").Select(s => s["S"]!.ToString()).ToList();
        Assert.Contains("3", sols);   // 1+2
        Assert.Contains("5", sols);   // 2+3
    }

    [Fact]
    public void SiblingLibraryWithOperators_ResolvesWithNoFlags()
    {
        // The dependency defines an operator the root's clauses NEED to
        // parse — only a load (with the sibling dir on the search path,
        // added implicitly) makes the root compilable. ADR-046: module
        // operators are scoped, so the dependency EXPORTS the op (how real
        // SWI/Scryer libraries hand their syntax to importers).
        using var t = new TempDir();
        t.Add("opsdep.pl",
            ":- module(opsdep, [means/2, op(700, xfx, ===>)]).\n" +
            "means(A ===> B, r(A, B)).\n");
        string root = t.Add("usermod.pl",
            ":- use_module(library(opsdep)).\n" +
            "route(R) :- means(a ===> b, R).\n");
        var errors = new System.Collections.Generic.List<ShmoCompileError>();
        var objects = ShmoViaConsult.Compile(
            root, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
        Assert.Empty(errors);
        Assert.Contains(objects, o => o.ModuleName == "usermod");
        Assert.Contains(objects, o => o.ModuleName == "opsdep");

        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects.Select(o => o.Object).ToArray(),
            EntryPoints = new[] { new PredicateRef("route", 1) },
        });
        Assert.True(link.Success);
        var e = new PrologEngine();
        e.LoadBundle(link.Bundle!);
        Assert.True(e.Query("route(r(a, b)).").Success);
    }

    [Fact]
    public void ALoadWarningGoesToTheGivenSinkAndNowhereElse()
    {
        // A host that compiles on its own behalf — WebShumway building the
        // forty libraries an imported collection provides — must not have
        // those libraries' load warnings land in the user's terminal. The
        // warnings still exist; they go where the caller says.
        using var t = new TempDir();
        string root = t.Add("noisy.pl", """
            :- module(noisy, [ok/1]).
            :- there_is_no_such_directive(x).
            ok(yes).
            """);

        var said = new StringWriter();
        var errors = new System.Collections.Generic.List<ShmoCompileError>();
        var prev = System.Console.Error;
        var stderr = new StringWriter();
        System.Console.SetError(stderr);
        try
        {
            var objects = ShmoViaConsult.Compile(
                root, System.Array.Empty<string>(), ShmoBuildMode.Release, errors,
                dialect: null, warnings: said);
            Assert.Empty(errors);
            Assert.Contains(objects, o => o.ModuleName == "noisy");
        }
        finally { System.Console.SetError(prev); }

        Assert.Contains("there_is_no_such_directive", said.ToString());
        Assert.DoesNotContain("there_is_no_such_directive", stderr.ToString());
    }

    [Fact]
    public void WithoutASinkAWarningStillGoesToStandardError()
    {
        // The CLI compiles what its user asked for, so its warnings are the
        // user's business: passing no sink must not silence them.
        using var t = new TempDir();
        string root = t.Add("noisy2.pl", """
            :- module(noisy2, [ok/1]).
            :- there_is_no_such_directive(x).
            ok(yes).
            """);

        var errors = new System.Collections.Generic.List<ShmoCompileError>();
        var prev = System.Console.Error;
        var stderr = new StringWriter();
        System.Console.SetError(stderr);
        try
        {
            ShmoViaConsult.Compile(
                root, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
        }
        finally { System.Console.SetError(prev); }

        Assert.Contains("there_is_no_such_directive", stderr.ToString());
    }
}

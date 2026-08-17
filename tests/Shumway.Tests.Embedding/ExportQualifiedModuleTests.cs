using System.IO;
using Shumway.Compiler.Ast;
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
        e.ConsultString("""
            :- use_module(library(greetq)).
            main(X) :- hello(X).
            """);
        Assert.True(e.Query("main(world).").Success);
    }

    [Fact]
    public void NonExportedPredicate_IsInvisibleToImporter()
    {
        using var libs = new LibSet().Add("greetq", GreetQ);
        var e = EngineWith(libs);
        e.ConsultString("""
            :- use_module(library(greetq)).
            peek(X) :- secret(X).
            """);
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
        e.ConsultString("""
            :- use_module(library(greetq), [hello/1]).
            a(X) :- hello(X).
            b(X) :- bye(X).
            """);
        Assert.True(e.Query("a(world).").Success);
        Assert.False(Holds(e, "b(gone)"));   // bye/1 not imported
    }

    [Fact]
    public void ImportOfNonExport_IsAnError()
    {
        using var libs = new LibSet().Add("greetq", GreetQ);
        var e = EngineWith(libs);
        var ex = Record.Exception(() => e.ConsultString("""
            :- use_module(library(greetq), [secret/1]).
            p(X) :- secret(X).
            """));
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
        e.ConsultString("""
            :- use_module(library(usesmember)).
            run(E, L) :- check(E, L).
            """);
        Assert.True(e.Query("run(2, [1,2,3]).").Success);
        Assert.False(e.Query("run(9, [1,2,3]).").Success);
    }

    [Fact]
    public void ImportingLibraryWithADirectiveGoal_LeavesProgramPredicatesResolvable()
    {
        // Regression: a library whose consult runs a directive AS A GOAL (an
        // unrecognised `:- G`, like `:- meta_predicate(...)`) triggers a query
        // setup DURING the enclosing consult, which used to cache a stale static
        // rewrite — leaving the importing program's OWN predicates unresolvable
        // (existence_error even though current_predicate reports them defined).
        using var libs = new LibSet().Add("gendir",
            ":- module(gendir, [g/0]).\n" +
            ":- some_unrecognised_directive_xyz.\n" +   // runs as a goal, warns
            "g.\n");
        var e = EngineWith(libs);
        e.ConsultString("""
            :- use_module(library(gendir)).
            run :- g.
            self :- write(ok).
            """);
        Assert.True(e.Query("self.").Success);   // the program's own predicate resolves
        Assert.True(e.Query("run.").Success);    // and its call into the library
    }

    [Fact]
    public void VariableMetaCall_ResolvesThroughImportTable()
    {
        using var libs = new LibSet().Add("greetq", GreetQ);
        var e = EngineWith(libs);
        // The body call is a VARIABLE meta-call (call(G) with G bound at runtime)
        // — resolution goes through the runtime $mqual import path, not the
        // compile-time ModuleRewrite one.
        e.ConsultString("""
            :- use_module(library(greetq)).
            run(X) :- G = hello(X), call(G).
            """);
        Assert.True(e.Query("run(world).").Success);
    }

    [Fact]
    public void ReExportOfBuiltin_ResolvesBareGlobal()
    {
        // Regression: a library that LISTS an export it does not itself define
        // (SWI's library(terms) re-exports the builtin term_variables/2). The
        // import must NOT map the name to a dangling terms$term_variables — it
        // must fall through to the bare-global builtin. Both a direct bare call
        // and a call from inside a library-local predicate must work.
        using var libs = new LibSet().Add("reterms",
            ":- module(reterms, [term_variables/2, mypred/1, grab/2]).\n" +
            "mypred(ok).\n" +
            "grab(T, Vs) :- term_variables(T, Vs).\n");   // local using the re-export
        var e = EngineWith(libs);
        e.ConsultString("""
            :- use_module(library(reterms)).
            direct(T, Vs) :- term_variables(T, Vs).
            """);   // bare, via user import
        // A term with two distinct variables yields a 2-element list.
        Assert.True(e.Query("direct(f(X,Y,X), Vs), Vs = [_,_].").Success);
        // grab/2 is a library-local caller of the re-exported builtin.
        Assert.True(e.Query("grab(g(A,B), Vs), Vs = [_,_].").Success);
        Assert.True(e.Query("mypred(ok).").Success);   // the genuine export still works
    }

    [Fact]
    public void ReExportOfImportedPredicate_ResolvesToTheDefiningModule()
    {
        // SICStus-style re-export: B imports pepe/1 from A and LISTS it in its own
        // export list without defining it. An importer of B must resolve pepe to
        // the DEFINING module (A$pepe) — not to a dangling B$pepe, and not fall
        // through to bare-global (where nothing lives).
        using var libs = new LibSet()
            .Add("defmod", ":- module(defmod, [pepe/1]).\npepe(defined_in_a).\n")
            .Add("remod",
                ":- module(remod, [pepe/1, own/1]).\n" +
                ":- use_module(library(defmod)).\n" +
                "own(mine).\n");
        var e = EngineWith(libs);
        e.ConsultString("""
            :- use_module(library(remod)).
            run(X) :- pepe(X).
            """);
        Assert.Equal("defined_in_a", e.QueryFirst<string>("run(X).", "X"));
        Assert.True(e.Query("own(mine).").Success);
    }

    [Fact]
    public void ReExportChain_TwoHops_ResolvesToTheDefiningModule()
    {
        // A defines, B re-exports A's export, C re-exports B's — the chase is
        // transitive.
        using var libs = new LibSet()
            .Add("bottom", ":- module(bottom, [val/1]).\nval(deep).\n")
            .Add("middle",
                ":- module(middle, [val/1]).\n" +
                ":- use_module(library(bottom)).\n")
            .Add("top",
                ":- module(top, [val/1]).\n" +
                ":- use_module(library(middle)).\n");
        var e = EngineWith(libs);
        e.ConsultString("""
            :- use_module(library(top)).
            get(X) :- val(X).
            """);
        Assert.Equal("deep", e.QueryFirst<string>("get(X).", "X"));
    }

    [Fact]
    public void SameNameExports_CoexistWithoutCollision()
    {
        using var libs = new LibSet()
            .Add("liba", ":- module(liba, [foo/1]).\nfoo(from_a).\n")
            .Add("libb", ":- module(libb, [foo/1]).\nfoo(from_b).\n");

        var ea = EngineWith(libs);
        ea.ConsultString("""
            :- use_module(library(liba)).
            get(X) :- foo(X).
            """);
        Assert.Equal("from_a", ea.QueryFirst<string>("get(X).", "X"));

        var eb = EngineWith(libs);
        eb.ConsultString("""
            :- use_module(library(libb)).
            get(X) :- foo(X).
            """);
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
        e.ConsultString("""
            :- use_module(library(liba)).
            :- use_module(library(libb)).
            get(X) :- foo(X).
            """);
        // liba was imported first; foo resolves to liba$foo.
        Assert.Equal("from_a", e.QueryFirst<string>("get(X).", "X"));
    }

    [Fact]
    public void DcgNonterminalExports_ResolveAsExpandedPredicates()
    {
        // Scryer's dcgs exports its nonterminals as `seq//1`-style indicators:
        // Name//A denotes the grammar-expanded Name/(A+2). Before the `//`
        // indicator was understood, clpz's imported seq//1 raised
        // existence_error(seq/3) from inside every reification propagator.
        using var libs = new LibSet().Add("grams",
            ":- module(grams, [seq//1, greeting//0]).\n" +
            "seq([]) --> [].\n" +
            "seq([E|Es]) --> [E], seq(Es).\n" +
            "greeting --> [h, i].\n");
        var e = EngineWith(libs);
        e.ConsultString("""
            :- use_module(library(grams)).
            roundtrip(L) :- phrase(seq(L), [a, b, c]).
            greets :- phrase(greeting, [h, i]).
            """);
        Assert.True(Holds(e, "roundtrip([a,b,c])"));
        Assert.True(Holds(e, "greets"));
    }

    [Fact]
    public void DirectlyConsultedModule_AutoImportsExportsIntoUser()
    {
        // SWI behaviour: loading a module file DIRECTLY (consult, not as a
        // use_module dependency) imports its exports into `user`, so they
        // are callable bare right after loading.
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(direct, [hello/1]).
            hello(world).
            secret(x).
            """);
        Assert.True(Holds(e, "hello(world)"));
        // Non-exported predicates stay module-local.
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => e.Query("secret(_)."));
    }

    [Fact]
    public void UseModuleDependency_DoesNotLeakExportsIntoUser()
    {
        // A module pulled in as a DEPENDENCY of a use_module load feeds only
        // the importer's table — its exports must not appear in `user`.
        using var libs = new LibSet()
            .Add("depb", ":- module(depb, [pb/1]).\npb(from_b).\n")
            .Add("depa",
                ":- module(depa, [pa/1]).\n" +
                ":- use_module(library(depb)).\n" +
                "pa(X) :- pb(X).\n");
        var e = EngineWith(libs);
        e.ConsultString(":- use_module(library(depa)).");
        Assert.True(Holds(e, "pa(from_b)"));
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => e.Query("pb(_)."));
    }

    [Fact]
    public void StaticQualifiedCall_ResolvesModuleLocalAtCompileTime()
    {
        // A statically written Module:Goal body goal resolves at compile time
        // (no runtime ':'/2 dispatch), reaching module-locals like the
        // runtime chain does.
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(qm, [entry/1]).
            entry(X) :- hidden(X).
            hidden(inner_value).
            """);
        e.ConsultString("probe(X) :- qm:hidden(X).");
        var sol = e.Query("probe(X).");
        Assert.True(sol.Success);
        Assert.Equal("inner_value", Assert.IsType<AtomTerm>(sol["X"]).Name);
    }

    [Fact]
    public void StaticQualifiedCall_ModuleLoadedLater_StillResolves()
    {
        // The caller consults BEFORE the target module exists: the qualified
        // goal stays on the runtime path, and once the module loads the
        // transform cache re-keys (_modulesVersion) so a later query
        // resolves statically. Either way the call must succeed.
        var e = new PrologEngine();
        e.ConsultString("probe2(X) :- lateqm:answer(X).");
        e.ConsultString("""
            :- module(lateqm, []).
            answer(late_value).
            """);
        var sol = e.Query("probe2(X).");
        Assert.True(sol.Success);
        Assert.Equal("late_value", Assert.IsType<AtomTerm>(sol["X"]).Name);
    }

    [Fact]
    public void StaticQualifiedCall_UnknownModule_FallsBackToBareGlobal()
    {
        // Runtime semantics for an unknown module: mangled miss → imports
        // miss → bare-global. The static path must preserve that.
        var e = new PrologEngine();
        e.ConsultString("""
            shared_pred(bare_one).
            probe3(X) :- nosuchmod:shared_pred(X).
            """);
        var sol = e.Query("probe3(X).");
        Assert.True(sol.Success);
        Assert.Equal("bare_one", Assert.IsType<AtomTerm>(sol["X"]).Name);
    }

    [Fact]
    public void CopyTermWithoutAttrVars_IsCopyTermWithPlainVars()
    {
        // The Scryer system builtin behind iso_ext's copy_term_nat/2.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "'$copy_term_without_attr_vars'(f(X, g(X), 7), C), C = f(A, g(B), 7), A == B.")
            .Success);
        // An attributed variable copies as a fresh PLAIN variable.
        e.ConsultString("""
            t :- put_attr(V, m, 1), '$copy_term_without_attr_vars'(h(V), h(C)),
                 var(C), \+ attvar(C), V \== C.
            """);
        Assert.True(e.Query("t.").Success);
    }
}

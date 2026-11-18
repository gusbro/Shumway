using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A file compiled on its own (<c>shumway-compile</c>, no
/// <c>--consult</c>) behaves as a compiled program: its
/// <c>:- initialization/1</c> goals run when its bundle loads, a goal
/// directive is ignored with a warning, and conditional compilation keeps the
/// clauses the consult keeps. Compiled through the consult, the goals ran then
/// and do not run again.</summary>
public sealed class CompiledInitializationTests
{
    private static ShmoObject Compile(string source, string module)
    {
        var r = ShmoCompiler.TryCompileSource(source, module);
        Assert.True(r.Success, string.Join("; ", r.Errors));
        return ShmoReader.FromBytes(ShmoWriter.ToBytes(r.Object!));
    }

    private static Bundle Link(params ShmoObject[] objects)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics));
        return BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!));
    }

    private static PrologEngine Load(Bundle bundle, System.IO.TextWriter? warnings = null)
    {
        var e = new PrologEngine();
        if (warnings is not null) e.Warnings = warnings;
        e.LoadBundle(bundle);
        e.PromoteBareBundleModulesToUser();
        return e;
    }

    // The binding as writeq/1 writes it.
    private static string Answer(PrologEngine e, string goal, string variable = "L")
    {
        string query = goal.TrimEnd('.') + $", term_to_atom({variable}, Text260).";
        var s = e.Query(query);
        Assert.True(s.Success, goal);
        return s.Bindings["Text260"].ToString() ?? "";
    }

    private const string Logging = @":- dynamic(log/1).
:- initialization(assertz(log(first))).
note(X) :- assertz(log(X)).
:- initialization(note(second)).
";

    [Fact]
    public void ACompiledFile_RunsItsInitializationGoalsWhenItsBundleLoads()
    {
        var obj = Compile(Logging, "init260a");
        Assert.Equal(2, obj.InitializationGoals.Count);
        var bundle = Link(obj);
        Assert.Equal(2, Assert.Single(bundle.Entries, x => x.ModuleName == "init260a").InitializationGoals.Count);

        var e = Load(bundle);
        Assert.Equal("[first,second]", Answer(e, "findall(X, log(X), L)."));
    }

    // Guards the test above: before the load nothing ran, and the goals are not
    // clauses of the program.
    [Fact]
    public void WithoutTheLoad_NothingRan()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(log/1).\n");
        Assert.Equal("[]", Answer(e, "findall(X, log(X), L)."));
        var obj = Compile(Logging, "init260g");
        Assert.DoesNotContain(obj.Defined, d => d.Indicator.Name == "initialization");
    }

    [Fact]
    public void TheGoalsOfSeveralModules_RunOnceTheBundleIsIn_InTheirOrder()
    {
        var first = Compile(@":- dynamic(log/1).
:- public(later/1).
:- initialization(assertz(log(a1))).
:- initialization(later(a2)).
later(X) :- assertz(log(X)).
", "init260b");
        // The second module's goal calls a predicate of the first one.
        var second = Compile(@":- initialization(later(b1)).
", "init260c");
        var warnings = new System.IO.StringWriter();
        var e = Load(Link(first, second), warnings);
        Assert.Equal("", warnings.ToString());
        Assert.Equal("[a1,a2,b1]", Answer(e, "findall(X, log(X), L)."));
    }

    [Fact]
    public void AGoalRunsInItsModule()
    {
        var obj = Compile(@":- module(init260m, []).
mark :- nb_setval(init260m, done).
:- initialization(mark).
", "init260m");
        var e = Load(Link(obj));
        Assert.Equal("done", Answer(e, "nb_getval(init260m, V).", "V"));
    }

    [Fact]
    public void AGoalThatFailsOrRaises_IsReported_AndTheNextRuns()
    {
        var warnings = new System.IO.StringWriter();
        var e = Load(Link(Compile(@":- dynamic(log/1).
:- initialization(fail).
:- initialization(atom_length(1, _)).
:- initialization(assertz(log(after))).
", "init260f")), warnings);
        string text = warnings.ToString();
        Assert.Contains("Warning: initialization goal failed: fail", text);
        Assert.Contains("Warning: initialization goal raised: atom_length(1, _", text);
        Assert.Contains("type_error", text);
        Assert.Equal("[after]", Answer(e, "findall(X, log(X), L)."));
    }

    [Fact]
    public void AGoalThatHalts_EndsTheLoad()
    {
        var bundle = Link(Compile(@":- initialization(halt(3)).
p.
", "init260h"));
        var halted = Assert.Throws<PrologHaltException>(() => new PrologEngine().LoadBundle(bundle));
        Assert.Equal(3, halted.ExitCode);
    }

    // What an executable's Main does: the arguments are in place for the goals.
    [Fact]
    public void AnEngineMadeFromTheBundle_HasItsArgumentsBeforeTheGoalsRun()
    {
        var bundle = Link(Compile(@":- dynamic(seen/1).
:- initialization((current_prolog_flag(argv, A), assertz(seen(A)))).
", "init260v"));
        var e = PrologEngine.FromBundle(bundle, null, new[] { "one", "two" });
        Assert.Equal("[one,two]", Answer(e, "seen(L)."));
    }

    // Linked from an entry point, what only a goal calls is reached from it:
    // the program keeps it, and the goal runs.
    [Fact]
    public void LinkedFromAnEntry_WhatOnlyAGoalCallsStaysLinked()
    {
        var obj = Compile(@":- public(main/0).
main.
start :- nb_setval(init260e, ran).
:- initialization(start).
", "init260e");
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics));
        Assert.DoesNotContain(link.Diagnostics, d => d.Message.Contains("not reachable"));
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!)));
        Assert.Equal("ran", Answer(e, "nb_getval(init260e, V).", "V"));
    }

    // With the prelude baked and pruned to what the program reaches, what a
    // goal calls of it is reached.
    [Fact]
    public void WithThePreludePruned_WhatAGoalCallsOfItStays()
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { Compile(@":- public(main/0).
main.
:- initialization((select(b, [a, b, c], L), nb_setval(init260p, L))).
", "init260p") },
            EntryPoints = new[] { new PredicateRef("main", 0) },
            BakePrelude = true,
            PrunePrelude = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics));
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!)));
        Assert.Equal("[a,c]", Answer(e, "nb_getval(init260p, V).", "V"));
    }

    // A goal that calls nothing linked is a link error, as a --goal's is.
    [Fact]
    public void AGoalThatCallsNothingLinked_FailsTheLink()
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { Compile(@":- initialization(nowhere_260).
p.
", "init260n") },
            Library = true,
        });
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics, d => d.Message.Contains("nowhere_260/0"));
    }

    // A goal directive is ignored, and the warning says how to run it.
    [Fact]
    public void AGoalDirective_IsIgnoredWithAWarning()
    {
        var r = ShmoCompiler.TryCompileSource(@":- dynamic(log/1).
:- assertz(log(directive)).
", "init260d");
        Assert.True(r.Success);
        var w = Assert.Single(r.Warnings);
        Assert.Contains("assertz/1", w.Message);
        Assert.Contains("initialization/1", w.Message);
        var e = Load(Link(r.Object!));
        Assert.Equal("[]", Answer(e, "findall(X, log(X), L)."));
    }

    // Through the consult the goal ran when the module loaded, and the bundle
    // holds what it left: it does not run again.
    [Fact]
    public void ThroughTheConsult_TheGoalsRanThen_AndDoNotRunAgain()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "shumway-init260-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string root = System.IO.Path.Combine(dir, "init260s.pl");
            System.IO.File.WriteAllText(root, Logging);
            var errors = new System.Collections.Generic.List<ShmoCompileError>();
            var objects = ShmoViaConsult.Compile(root, System.Array.Empty<string>(),
                ShmoBuildMode.Release, errors, warnings: System.IO.TextWriter.Null);
            Assert.Empty(errors);
            Assert.All(objects, o => Assert.Empty(o.Object.InitializationGoals));
            var e = Load(Link(System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(objects, o => o.Object))));
            Assert.Equal("[first,second]", Answer(e, "findall(X, log(X), L)."));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    // ---------- conditional compilation: the clauses the consult keeps

    [Theory]
    [InlineData(@":- if(current_prolog_flag(bounded, false)).
c(unbounded).
:- else.
c(bounded).
:- endif.
", "[unbounded]")]
    [InlineData(@":- if(fail).
c(1).
:- elif(fail).
c(2).
:- elif(true).
c(3).
:- else.
c(4).
:- endif.
", "[3]")]
    [InlineData(@":- if(fail).
:- if(true).
c(inner).
:- endif.
:- else.
c(outer).
:- endif.
", "[outer]")]
    [InlineData(@":- if(\+ current_predicate(nowhere/7)).
c(missing).
:- endif.
:- if(current_predicate(append/3)).
c(system).
:- endif.
", "[missing,system]")]
    [InlineData(@"own(1).
:- if(current_predicate(own/1)).
c(above).
:- endif.
:- if(current_predicate(below/1)).
c(below).
:- endif.
below(1).
", "[above]")]
    [InlineData(@":- dynamic(d/1).
:- if(current_predicate(d/1)).
c(declared).
:- endif.
", "[declared]")]
    [InlineData(@":- set_prolog_flag(double_quotes, codes).
:- if(current_prolog_flag(double_quotes, codes)).
c(codes).
:- else.
c(other).
:- endif.
", "[codes]")]
    [InlineData(@":- if(exists_source(library(clpfd))).
c(clpfd).
:- endif.
:- if(exists_source(library(no_such_library_260))).
c(none).
:- endif.
", "[clpfd]")]
    public void ACompiledFile_KeepsTheClausesTheConsultKeeps(string source, string expected)
    {
        var consulted = new PrologEngine();
        consulted.ConsultString(source);
        Assert.Equal(expected, Answer(consulted, "findall(X, c(X), L)."));

        var compiled = Load(Link(Compile(source, "cond260")));
        Assert.Equal(expected, Answer(compiled, "findall(X, c(X), L)."));
    }

    [Fact]
    public void AConditionNeitherCanAnswer_IsReported_AndTakenAsFalse()
    {
        const string source = @":- if(atom_length(abc, 3)).
c(ran).
:- else.
c(false).
:- endif.
";
        var consultWarnings = new System.IO.StringWriter();
        var consulted = new PrologEngine { Warnings = consultWarnings };
        consulted.ConsultString(source);
        Assert.Contains("unsupported :- if/elif condition", consultWarnings.ToString());
        Assert.Equal("[false]", Answer(consulted, "findall(X, c(X), L)."));

        var r = ShmoCompiler.TryCompileSource(source, "cond260u");
        Assert.True(r.Success);
        var w = Assert.Single(r.Warnings);
        Assert.Contains("unsupported :- if/elif condition", w.Message);
        Assert.Equal(1, w.Line);
        Assert.Equal("[false]", Answer(Load(Link(r.Object!)), "findall(X, c(X), L)."));
    }

    // What an inactive branch holds is not the program's: neither its clauses
    // nor its directives.
    [Fact]
    public void AnInactiveBranch_LeavesNoGoalNorFlag()
    {
        var obj = Compile(@":- dynamic(log/1).
:- if(fail).
:- initialization(assertz(log(skipped))).
:- set_prolog_flag(unknown, fail).
c(skipped).
:- endif.
", "cond260i");
        Assert.Empty(obj.InitializationGoals);
        Assert.Empty(obj.Flags);
        var e = Load(Link(obj));
        Assert.Equal("[]", Answer(e, "findall(X, log(X), L)."));
        Assert.True(e.Query("current_prolog_flag(unknown, error).").Success);
    }
}

using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The late-helper registry's name filter must not swallow USER
/// predicates. MetaTransform helpers are '$kind_id' — bare '$disj_12',
/// module-mangled 'mod$$disj_12', always a '$' right before the last
/// segment. A user local like 'test_326' fits the letters_digits shape by
/// accident; registering it (bare, first-compile-wins) leaked it through
/// the module wall — resolving cross-module ahead of both the
/// existence_error and the consult-direct fallback's ambiguity check.</summary>
public sealed class LateHelperNameLeakTests
{
    [Fact]
    public void ShapedLocals_OfAUseModuleDependency_StayPrivate()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "shumway_lhl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // dep_7/0 matches the helper shape (letters_digits) — before the
            // fix it registered under its bare name and a top-level call ran
            // it despite the module being a use_module DEPENDENCY.
            File.WriteAllText(Path.Combine(dir, "lhl_dep.pl"), """
                :- module(lhl_dep, [lhl_pub/0]).
                lhl_pub :- dep_7.
                dep_7.
                """);
            File.WriteAllText(Path.Combine(dir, "lhl_top.pl"), """
                :- module(lhl_top, []).
                :- use_module('lhl_dep.pl').
                touch :- lhl_pub.
                """);
            var e = new PrologEngine();
            e.ConsultFile(Path.Combine(dir, "lhl_top.pl"));
            Assert.True(e.Query("touch.").Success);
            Assert.True(e.Query(
                "catch(dep_7, error(existence_error(procedure, dep_7/0), _), true).")
                .Success);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ShapedDuplicates_HitTheAmbiguityCheck_NotFirstCompileWins()
    {
        // Two directly consulted modules define dup_1/0: the consult-direct
        // fallback must raise the ambiguity ball. Before the fix, the
        // first-compiled module's dup_1 registered bare in the late-helper
        // registry and PREEMPTED it — the bare call silently ran whichever
        // module compiled first.
        var e = new PrologEngine();
        e.ConsultString(":- module(lhl_a, []).\ndup_1 :- throw(ran_a).\n");
        e.ConsultString(":- module(lhl_b, []).\ndup_1 :- throw(ran_b).\n");
        Assert.True(e.Query(
            "catch(dup_1, error(existence_error(procedure, dup_1/0), "
            + "shumway(ambiguous_module_local, [lhl_a, lhl_b])), true).").Success);
    }

    [Fact]
    public void RealRuntimeAssertHelpers_StillMaterialize()
    {
        // The registry's actual job: a clause asserted with a control
        // construct in its body compiles '$disj_N'-style helpers that other
        // activations materialize on demand. Assert + call across queries.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "assertz((lhl_d(X) :- ( X == a -> true ; X == b ))).").Success);
        Assert.True(e.Query("lhl_d(a).").Success);
        Assert.True(e.Query("lhl_d(b).").Success);
        Assert.False(e.Query("lhl_d(c).").Success);
    }
}

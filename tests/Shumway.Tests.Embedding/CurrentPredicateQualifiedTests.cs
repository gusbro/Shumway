using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Qualified <c>current_predicate(M:PI)</c> — stage 1 of the real
/// M:P story. The qualified form answers for what module M DEFINES (clause
/// heads, its dynamics — imports and re-exports are not definitions); an
/// unbound M backtracks over the modules, SWI-style. Both spellings are one
/// form: the operator-natural <c>m:f/0</c> parses as <c>(m:f)/0</c> (':' at
/// 200 binds tighter than '/' at 400), and <c>m:(f/0)</c> qualifies the
/// whole indicator. Error shapes pinned against SWI.</summary>
public sealed class CurrentPredicateQualifiedTests
{
    private const string ModA = """
        :- module(cpq_a, [a_pub/1]).
        a_pub(1).
        a_loc(x).
        a_loc(y).
        """;

    private const string ModB = """
        :- module(cpq_b, []).
        b_loc.
        """;

    [Fact]
    public void BoundModule_BoundIndicator_IsAMembershipTest()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Assert.True(e.Query("current_predicate(cpq_a:a_pub/1).").Success);
        Assert.True(e.Query("current_predicate(cpq_a:a_loc/1).").Success);
        Assert.False(e.Query("current_predicate(cpq_a:absent/3).").Success);
        Assert.False(e.Query("current_predicate(cpq_a:a_loc/9).").Success);
    }

    [Fact]
    public void BoundModule_FreeName_EnumeratesItsDefinitions()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Assert.True(e.Query(
            "findall(N/A, current_predicate(cpq_a:N/A), L), msort(L, [a_loc/1, a_pub/1]).")
            .Success);
    }

    [Fact]
    public void TheScryerDriverShape_CountsTheModuleTests()
    {
        // The exact findall iso-conformity-tests.pl's run_tests is built on.
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(cpq_suite, []).
            test_1.
            test_2.
            test_3.
            helper.
            """);
        var sol = e.Query(
            "findall(T, (current_predicate(cpq_suite:T/0), sub_atom(T, 0, 5, _, test_)), Ts), "
            + "length(Ts, N).");
        Assert.True(sol.Success);
        Assert.Equal("3", sol["N"]!.ToString());
    }

    [Fact]
    public void FreeModule_BacktracksOverModules()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString(ModB);
        var sol = e.Query("current_predicate(M:b_loc/0).");
        Assert.True(sol.Success);
        Assert.Equal("cpq_b", sol["M"]!.ToString());
        // A name defined nowhere fails silently — the SWI behaviour.
        Assert.False(e.Query("current_predicate(_:nowhere/0).").Success);
    }

    [Fact]
    public void UserModule_HoldsThePlainConsultedPredicates()
    {
        var e = new PrologEngine();
        e.ConsultString("plain_fact(1).");
        Assert.True(e.Query("current_predicate(user:plain_fact/1).").Success);
    }

    [Fact]
    public void UnknownModule_Fails()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Assert.False(e.Query("current_predicate(no_such_module:_/_).").Success);
    }

    [Fact]
    public void NonAtomModule_IsATypeError_SwiShape()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(current_predicate(1:foo/0), error(type_error(atom, 1), _), true).")
            .Success);
    }

    [Fact]
    public void QualifiedNonIndicator_BlamesTheWholeTerm_SwiShape()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Assert.True(e.Query(
            "catch(current_predicate(cpq_a:bad), "
            + "error(type_error(predicate_indicator, cpq_a:bad), _), true).")
            .Success);
    }

    [Fact]
    public void NestedQualification_InnermostModuleWins()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString(ModB);
        Assert.True(e.Query("current_predicate(cpq_a:cpq_b:b_loc/0).").Success);
        Assert.False(e.Query("current_predicate(cpq_b:cpq_a:b_loc/0).").Success);
    }

    [Fact]
    public void WholeIndicatorSpelling_IsTheSameForm()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Assert.True(e.Query("current_predicate(cpq_a:(a_loc/1)).").Success);
        Assert.True(e.Query(
            "findall(N, current_predicate(cpq_a:(N/1)), L), msort(L, [a_loc, a_pub]).")
            .Success);
    }

    [Fact]
    public void Imports_AreNotDefinitions()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "shumway_cpq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "cpq_dep.pl"), """
                :- module(cpq_dep, [dep_pub/0]).
                dep_pub.
                """);
            File.WriteAllText(Path.Combine(dir, "cpq_top.pl"), """
                :- module(cpq_top, []).
                :- use_module('cpq_dep.pl').
                top_own :- dep_pub.
                """);
            var e = new PrologEngine();
            e.ConsultFile(Path.Combine(dir, "cpq_top.pl"));
            Assert.True(e.Query("current_predicate(cpq_top:top_own/0).").Success);
            Assert.True(e.Query("current_predicate(cpq_dep:dep_pub/0).").Success);
            // Imported into cpq_top, but DEFINED in cpq_dep only.
            Assert.False(e.Query("current_predicate(cpq_top:dep_pub/0).").Success);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ModuleDynamics_CountAsItsDefinitions()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(cpq_d, []).
            :- dynamic(d_dyn/2).
            """);
        Assert.True(e.Query("current_predicate(cpq_d:d_dyn/2).").Success);
    }

    [Fact]
    public void Helpers_NeverSurface()
    {
        var e = new PrologEngine();
        // The catch/once bodies lift $-helpers into the module.
        e.ConsultString("""
            :- module(cpq_h, []).
            h_top :- catch(once(member(_, [1])), _, true).
            """);
        Assert.True(e.Query(
            "findall(N/A, current_predicate(cpq_h:N/A), [h_top/0]).").Success);
    }

    [Fact]
    public void UnqualifiedForm_IsUntouched()
    {
        var e = new PrologEngine();
        e.ConsultString("plain(1).");
        Assert.True(e.Query("current_predicate(plain/1).").Success);
        Assert.False(e.Query("current_predicate(absent_here/7).").Success);
        Assert.True(e.Query(
            "catch(current_predicate(bad), error(type_error(predicate_indicator, bad), _), true).")
            .Success);
    }
}

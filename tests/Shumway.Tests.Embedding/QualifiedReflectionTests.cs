using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Stage 2 of the M:P story — the reflection side.
/// <c>clause(M:H, B)</c> and <c>predicate_property(M:H, P)</c> resolve the
/// head from M's VIEWPOINT (own definition; else the import's source, with
/// <c>imported_from(Source)</c>; else the bare-global/builtin everyone
/// sees); <c>listing(M:Spec)</c> lists what M defines. And inside a module,
/// the unqualified forms see the module's own predicates: ModuleRewrite
/// stamps the textual module at compile time, the $mqual idea — no runtime
/// context register exists.</summary>
public sealed class QualifiedReflectionTests
{
    private const string ModS = """
        :- module(qr_s, []).
        loc(1).
        loc(2).
        """;

    // --- clause(M:H, B) ----------------------------------------------------

    [Fact]
    public void QualifiedClause_ReadsTheModulesOwnClauses()
    {
        var e = new PrologEngine();
        e.ConsultString(ModS);
        Assert.True(e.Query(
            "findall(X, clause(qr_s:loc(X), true), [1, 2]).").Success);
    }

    [Fact]
    public void QualifiedClause_WrongModule_Fails()
    {
        var e = new PrologEngine();
        e.ConsultString(ModS);
        e.ConsultString(":- module(qr_other, []).\nother(1).\n");
        Assert.False(e.Query("clause(qr_other:loc(_), _).").Success);
        Assert.False(e.Query("clause(no_such:loc(_), _).").Success);
    }

    [Fact]
    public void QualifiedClause_DynamicIsFlatGlobal_QualifierPeels()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic(qr_d/1).");
        Assert.True(e.Query("assertz(qr_d(7)).").Success);
        // ANY module qualifier reaches the shared store — dynamics have no wall.
        Assert.True(e.Query("clause(qr_s:qr_d(X), true), X == 7.").Success);
        Assert.True(e.Query("clause(user:qr_d(X), true), X == 7.").Success);
    }

    [Fact]
    public void QualifiedClause_ModuleErrors()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(clause(1:foo(_), _), error(type_error(atom, 1), _), true).").Success);
        Assert.True(e.Query(
            "catch(clause(M:foo(_), _), error(instantiation_error, _), true).").Success);
    }

    // --- predicate_property(M:H, P) ----------------------------------------

    [Fact]
    public void QualifiedProperty_OwnLocal_StaticDefined()
    {
        var e = new PrologEngine();
        e.ConsultString(ModS);
        Assert.True(e.Query(
            "findall(P, predicate_property(qr_s:loc(_), P), [static, defined]).")
            .Success);
    }

    [Fact]
    public void QualifiedProperty_Import_CarriesImportedFrom()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "shumway_qr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "qr_dep.pl"), """
                :- module(qr_dep, [dpub/1]).
                dpub(a).
                """);
            File.WriteAllText(Path.Combine(dir, "qr_top.pl"), """
                :- module(qr_top, []).
                :- use_module('qr_dep.pl').
                touch :- dpub(a).
                """);
            var e = new PrologEngine();
            e.ConsultFile(Path.Combine(dir, "qr_top.pl"));
            // Through the importer: the source's predicate, plus where from —
            // the SICStus doctrine (current_predicate strictly definitions;
            // predicate_property is where visibility shows).
            Assert.True(e.Query(
                "findall(P, predicate_property(qr_top:dpub(_), P), "
                + "[static, defined, imported_from(qr_dep)]).").Success);
            // At the definer: no imported_from.
            Assert.True(e.Query(
                "findall(P, predicate_property(qr_dep:dpub(_), P), [static, defined]).")
                .Success);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void QualifiedProperty_FallsThroughToTheGlobalView()
    {
        var e = new PrologEngine();
        e.ConsultString(ModS);
        // qr_s neither defines nor imports msort/2 — the builtin everyone
        // sees answers, exactly as an unqualified query would.
        Assert.True(e.Query("predicate_property(qr_s:msort(_, _), built_in).").Success);
        // Defined nowhere and visible nowhere: fails.
        Assert.False(e.Query("predicate_property(qr_s:absent(_), _).").Success);
    }

    [Fact]
    public void QualifiedProperty_MetaTemplate_SurvivesTheQualifier()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(qr_m, []).
            :- meta_predicate(wrap(0)).
            wrap(G) :- call(G).
            """);
        Assert.True(e.Query(
            "predicate_property(qr_m:wrap(_), meta_predicate(T)), T == wrap(0).")
            .Success);
    }

    // --- listing(M:Spec) ---------------------------------------------------

    private static string Capture(PrologEngine e, string goal)
    {
        var sw = new StringWriter();
        e.Out = sw;
        e.Query(goal);
        return sw.ToString();
    }

    [Fact]
    public void QualifiedListing_PrintsTheModulesPredicate()
    {
        var e = new PrologEngine();
        e.ConsultString(ModS);
        string output = Capture(e, "listing(qr_s:loc/1).");
        Assert.Contains("loc(1)", output);
        Assert.Contains("loc(2)", output);
        // The name-only spelling lists every arity of the name.
        Assert.Contains("loc(1)", Capture(e, "listing(qr_s:loc)."));
    }

    [Fact]
    public void QualifiedListing_WrongModule_SaysSo()
    {
        var e = new PrologEngine();
        e.ConsultString(ModS);
        Assert.Contains("nothing to list", Capture(e, "listing(no_such:loc/1)."));
    }

    // --- the injected in-module context ------------------------------------

    [Fact]
    public void InsideAModule_UnqualifiedReflection_SeesItsOwnLocals()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(qr_ctx, []).
            loc(here).
            sees_own_cp    :- current_predicate(loc/1).
            sees_global_cp :- current_predicate(msort/2).
            sees_own_clause :- clause(loc(X), true), X == here.
            sees_own_prop  :- predicate_property(loc(_), static).
            """);
        Assert.True(e.Query("sees_own_cp.").Success);
        Assert.True(e.Query("sees_global_cp.").Success);
        Assert.True(e.Query("sees_own_clause.").Success);
        Assert.True(e.Query("sees_own_prop.").Success);
    }

    [Fact]
    public void InsideAModule_ExplicitQualification_StillWins()
    {
        var e = new PrologEngine();
        e.ConsultString(ModS);
        e.ConsultString("""
            :- module(qr_x, []).
            reads_s(X) :- clause(qr_s:loc(X), true).
            counts_s(N) :- findall(T, current_predicate(qr_s:T/1), L), length(L, N).
            """);
        Assert.True(e.Query("reads_s(1).").Success);
        var sol = e.Query("counts_s(N).");
        Assert.True(sol.Success);
        Assert.Equal("1", sol["N"]!.ToString());
    }

    // --- regressions --------------------------------------------------------

    [Fact]
    public void UnqualifiedFromUser_IsUntouched()
    {
        // `:- public` keeps the static predicate clause/2-readable (ISO's
        // public-procedure notion); without it clause/2 raises
        // permission_error(access, private_procedure, _).
        var e = new PrologEngine();
        e.ConsultString(":- public plain/1.\nplain(1).\nplain(2).\n");
        Assert.True(e.Query("clause(plain(1), true).").Success);
        Assert.True(e.Query("predicate_property(plain(_), static).").Success);
        Assert.True(e.Query("current_predicate(plain/1).").Success);
    }
}

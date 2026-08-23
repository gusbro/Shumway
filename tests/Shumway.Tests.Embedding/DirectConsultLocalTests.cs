using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The consult-direct bare-call fallback: consulting a source
/// DIRECTLY means being able to call its predicates, whether or not the file
/// declares <c>:- module</c>. A bare goal no other route resolves is resolved
/// to the ONE directly consulted explicit module that defines it; two
/// candidates raise the ambiguity existence_error naming both; a module
/// loaded only as a <c>use_module</c> dependency keeps its locals private.
/// The fallback runs where the call would otherwise raise existence_error,
/// so it can never shadow a builtin, a bare-global public, a dynamic or a
/// <c>user</c> import.</summary>
public sealed class DirectConsultLocalTests
{
    private const string ModA = """
        :- module(dcl_a, []).
        a_local(1).
        a_local(2).
        shared_here(a).
        """;

    private const string ModB = """
        :- module(dcl_b, []).
        b_local(x).
        shared_here(b).
        """;

    [Fact]
    public void LocalOfADirectlyConsultedModule_IsCallableBare()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        var sol = e.Query("a_local(X).");
        Assert.True(sol.Success);
        Assert.Equal(1, ((IntTerm)sol["X"]!).Value);
    }

    [Fact]
    public void FallbackEnumeratesAllSolutions_NotJustTheFirst()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        var all = e.QueryAll("a_local(X).");
        int n = 0;
        foreach (var s in all) { Assert.True(s.Success); n++; }
        Assert.Equal(2, n);
    }

    [Fact]
    public void MetaCall_ResolvesTheLocalToo()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        Assert.True(e.Query("call(a_local(2)).").Success);
        Assert.True(e.Query("findall(X, a_local(X), [1, 2]).").Success);
    }

    [Fact]
    public void TwoDirectConsults_SameName_RaiseTheAmbiguityError()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString(ModB);
        // Still an existence_error — an undefined-procedure catcher matches —
        // and the context names both candidates, sorted.
        Assert.True(e.Query(
            "catch(shared_here(_), error(existence_error(procedure, shared_here/1), Ctx), true), "
            + "Ctx == shumway(ambiguous_module_local, [dcl_a, dcl_b]).").Success);
    }

    [Fact]
    public void QualifiedCall_DisambiguatesAsTheErrorSuggests()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        e.ConsultString(ModB);
        var sol = e.Query("dcl_b:shared_here(W).");
        Assert.True(sol.Success);
        Assert.Equal("b", sol["W"]!.ToString());
    }

    [Fact]
    public void UseModuleDependency_KeepsItsLocalsPrivate()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "shumway_dcl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "dcl_dep.pl"), """
                :- module(dcl_dep, [dep_export/0]).
                dep_export.
                dep_local.
                """);
            File.WriteAllText(Path.Combine(dir, "dcl_top.pl"), """
                :- module(dcl_top, []).
                :- use_module('dcl_dep.pl').
                top_local :- dep_export.
                """);
            var e = new PrologEngine();
            e.ConsultFile(Path.Combine(dir, "dcl_top.pl"));
            // The consulted file's local: callable (and through it, the export).
            Assert.True(e.Query("top_local.").Success);
            // The dependency's local: NOT callable bare — use_module is an
            // interface, not a consult.
            Assert.True(e.Query(
                "catch(dep_local, error(existence_error(procedure, dep_local/0), _), true).")
                .Success);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BareGlobalDefinition_WinsOverTheFallback()
    {
        var e = new PrologEngine();
        // A plain (module-less) consult defines the bare-global predicate...
        e.ConsultString("shared_here(global).");
        // ...and a module consulted later defines a local of the same name.
        e.ConsultString(ModA);
        var sol = e.Query("shared_here(W).");
        Assert.True(sol.Success);
        // The bare global resolves as always; the fallback never ran.
        Assert.Equal("global", sol["W"]!.ToString());
    }

    [Fact]
    public void DynamicsDeclaredInAModule_StayFlatGlobal_NoFallbackInvolved()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(dcl_f, []).
            :- dynamic(f_d/1).
            f_s(ok).
            """);
        // The dynamic bypasses the module wall by design: callable bare,
        // empty means FAIL (never existence_error, never a fallback resolve).
        Assert.False(e.Query("f_d(_).").Success);
        Assert.True(e.Query("assertz(f_d(9)), f_d(9).").Success);
        // The module's static local still arrives through the fallback.
        Assert.True(e.Query("f_s(ok).").Success);
    }

    [Fact]
    public void TheLocalsName_IsProtectedAsStatic_NotAssertable()
    {
        var e = new PrologEngine();
        e.ConsultString(ModA);
        // The bare name resolves to the module's STATIC local — assertz over
        // it is refused exactly as for any static procedure, instead of
        // quietly minting a bare dynamic that would then shadow the local.
        Assert.True(e.Query(
            "catch(assertz(a_local(99)), error(permission_error(modify, static_procedure, _), _), true).")
            .Success);
    }

    [Fact]
    public void ModulelessFiles_AreUntouchedByTheFallback()
    {
        var e = new PrologEngine();
        e.ConsultString("plain_public(yes).");
        Assert.True(e.Query("plain_public(yes).").Success);
        // A name nobody defines still errors exactly as before.
        Assert.True(e.Query(
            "catch(never_defined, error(existence_error(procedure, never_defined/0), _), true).")
            .Success);
    }
}

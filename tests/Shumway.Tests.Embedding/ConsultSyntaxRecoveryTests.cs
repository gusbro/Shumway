using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Issue #109 — a consult keeps going. A clause that does not parse
/// is a per-clause diagnostic (position included) and the reader resyncs to
/// the next terminator dot; a set_prolog_flag directive the reader cannot
/// apply eagerly is left for runtime to raise its own ISO error, never a
/// reason to abort the file.</summary>
public sealed class ConsultSyntaxRecoveryTests
{
    [Fact]
    public void MidFileSyntaxError_LoadsTheClausesAroundIt()
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Warnings = warnings };
        e.ConsultString("""
            before(1).
            broken(] .
            after(2).
            """);
        Assert.True(e.Query("before(1).").Success);
        Assert.True(e.Query("after(2).").Success);
        string diag = warnings.ToString();
        Assert.Contains("syntax error", diag);
        Assert.Matches(@"\d+:\d+:", diag);
    }

    [Fact]
    public void DirectivesAfterASyntaxError_StillApply()
    {
        // Recovery must not degrade the lazy read model: an operator declared
        // AFTER the broken clause still governs the parse of what follows.
        var e = new PrologEngine { Warnings = new StringWriter() };
        e.ConsultString("""
            broken(] .
            :- op(700, xfx, ===).
            uses(a === b).
            """);
        Assert.True(e.Query("uses(===(a, b)).").Success);
    }

    [Fact]
    public void SeveralBadClauses_EachIsItsOwnDiagnostic()
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Warnings = warnings };
        e.ConsultString("""
            good1.
            bad(] .
            good2.
            worse(} .
            good3.
            """);
        Assert.True(e.Query("good1, good2, good3.").Success);
        Assert.Equal(2, warnings.ToString().Split("syntax error").Length - 1);
    }

    [Fact]
    public void SetPrologFlag_UninstantiatedInADirective_DoesNotAbortTheConsult()
    {
        // The reader's eager parse-time application of double_quotes /
        // arity_compat must not reject what it cannot apply: the directive is
        // a runtime goal and raises its own instantiation_error (reported,
        // not fatal), and the clauses after it load.
        var e = new PrologEngine { Warnings = new StringWriter() };
        e.ConsultString("""
            first.
            :- set_prolog_flag(X, off).
            second.
            """);
        Assert.True(e.Query("first, second.").Success);
    }

    [Fact]
    public void StrictConsultSyntax_RestoresAllOrNothing_ForBuilds()
    {
        // A host that compiles rather than loads must not bake a module
        // quietly missing a clause: the bundle writer's validation consult
        // runs strict, and the first bad clause raises syntax_error.
        var e = new PrologEngine { StrictConsultSyntax = true };
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => e.ConsultString("good.\nbroken(] .\n"));
        Assert.Equal("syntax_error", ex.Kind);
    }

    [Fact]
    public void SetPrologFlag_BallContextNamesThePredicate()
    {
        // error(instantiation_error, set_prolog_flag/2) — not a `_` context.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(set_prolog_flag(X, off), error(instantiation_error, C), true), C == set_prolog_flag/2.").Success);
        Assert.True(e.Query(
            "catch(set_prolog_flag(foo, bar), error(domain_error(prolog_flag, foo), C), true), C == set_prolog_flag/2.").Success);
    }
}

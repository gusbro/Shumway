using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Scryer/Trealla compatibility libraries loaded via
/// <c>use_module(library(Name))</c> (<see cref="CompatLibraries"/>), plus the
/// consult-time execution of <c>use_module/1</c> directives that makes them
/// resolve for the rest of the file — a program written for Scryer imports its
/// stdlib this way and must consult unchanged.
/// </summary>
public class CompatLibrariesTests
{
    [Fact]
    public void Library_Dcgs_ProvidesSeqAndEllipsis()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- use_module(library(dcgs)).");
        Assert.True(engine.Query("phrase(seq([a, b]), [a, b]).").Success);
        Assert.False(engine.Query("phrase(seq([a, b]), [a, x]).").Success);
        // ...//0 skips any prefix.
        Assert.True(engine.Query("phrase(('...', [c]), [a, b, c]).").Success);
    }

    [Fact]
    public void Library_Dif_ProvidesDif()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- use_module(library(dif)).");
        Assert.True(engine.Query("dif(a, b).").Success);
        Assert.False(engine.Query("dif(a, a).").Success);
        // Unbound-vs-value: optimistic success (would delay in a real dif).
        Assert.True(engine.Query("dif(_, a).").Success);
    }

    [Fact]
    public void Library_Format_ProvidesDcgFormat()
    {
        var engine = new PrologEngine();
        engine.Query("set_prolog_flag(double_quotes, chars).");
        engine.ConsultString(":- use_module(library(format)).");
        // ~s splices a list, ~d formats an integer, literals pass through.
        Assert.True(engine.Query("phrase(format_(\"<~s=~d>\", [[k], 42]), \"<k=42>\").").Success);
    }

    [Fact]
    public void Library_NoOp_ForPreludeCoveredModules()
    {
        var engine = new PrologEngine();
        // lists / charsio are prelude-covered — importing them is a no-op that
        // succeeds and leaves the prelude predicates working.
        engine.ConsultString("""
            :- use_module(library(lists)).
            :- use_module(library(charsio)).
            :- public ok/0.
            ok.
            """);
        Assert.True(engine.Query("ok.").Success);
        Assert.True(engine.Query("member(2, [1, 2, 3]).").Success);
    }

    [Fact]
    public void UseModule_Directive_ExecutesInline_ResolvingLaterClauses()
    {
        // The import directive must run during consult so a clause defined
        // AFTER it can call the imported predicate.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- use_module(library(dcgs)).
            :- public two/2.
            two --> seq(_), seq(_).
            """);
        Assert.True(engine.Query("two([a, b], []).").Success);
    }

    [Fact]
    public void UseModule_Library_IsIdempotent()
    {
        var engine = new PrologEngine();
        // Importing the same library twice must not re-consult (which would
        // trip the public-predicate uniqueness check).
        engine.ConsultString("""
            :- use_module(library(dcgs)).
            :- use_module(library(dcgs)).
            """);
        Assert.True(engine.Query("phrase(seq([a]), [a]).").Success);
    }

    [Fact]
    public void UseModule_UnknownLibrary_Directive_WarnsButDoesNotAbort()
    {
        var engine = new PrologEngine();
        // As a directive, an unknown library must not abort the consult.
        engine.ConsultString("""
            :- use_module(library(no_such_library_xyz)).
            :- public ok/0.
            ok.
            """);
        Assert.True(engine.Query("ok.").Success);
    }

    // ---------- Two-arg module directive (ISO / SWI / Scryer) ----------

    [Fact]
    public void ModuleDirective_TwoArg_IsExportQualified_NotBareGlobal()
    {
        // ADR-038: `:- module(Name, [Exports])` is EXPORT-QUALIFIED — every
        // predicate is mangled Name$x (nothing bare-global). A DIRECTLY
        // consulted module auto-imports its exports into `user` (SWI
        // behaviour), so the export resolves through the import table; the
        // private predicate resolves too, but through the consult-direct
        // fallback — the mangling itself is pinned by the manifest flag and
        // by DirectConsultLocalTests' ambiguity/use_module contrasts.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- module(mymod, [pub/1]).
            pub(hello).
            priv(secret).
            """);
        Assert.True(engine.Modules.ContainsKey("mymod"));
        Assert.True(engine.Modules["mymod"].IsExportQualified);
        Assert.True(engine.Query("pub(hello).").Success);
        Assert.True(engine.Query("priv(secret).").Success);
    }

    [Fact]
    public void ModuleDirective_TwoArg_SkipsNonIndicatorExports()
    {
        // op/3 and other non-PI export entries are ignored, not fatal — the
        // consult succeeds and the export-qualified module is registered
        // (g/1 lives as m3$g, reachable via import — see ExportQualifiedModuleTests).
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- module(m3, [g/1, op(700, xfx, ===)]).
            g(ok).
            """);
        Assert.True(engine.Modules.ContainsKey("m3"));
        Assert.True(engine.Modules["m3"].IsExportQualified);
    }
}

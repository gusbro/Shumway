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
        engine.ConsultString(
            ":- use_module(library(lists)).\n" +
            ":- use_module(library(charsio)).\n" +
            ":- public ok/0.\n" +
            "ok.\n");
        Assert.True(engine.Query("ok.").Success);
        Assert.True(engine.Query("member(2, [1, 2, 3]).").Success);
    }

    [Fact]
    public void UseModule_Directive_ExecutesInline_ResolvingLaterClauses()
    {
        // The import directive must run during consult so a clause defined
        // AFTER it can call the imported predicate.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- use_module(library(dcgs)).\n" +
            ":- public two/2.\n" +
            "two --> seq(_), seq(_).\n");
        Assert.True(engine.Query("two([a, b], []).").Success);
    }

    [Fact]
    public void UseModule_Library_IsIdempotent()
    {
        var engine = new PrologEngine();
        // Importing the same library twice must not re-consult (which would
        // trip the public-predicate uniqueness check).
        engine.ConsultString(
            ":- use_module(library(dcgs)).\n" +
            ":- use_module(library(dcgs)).\n");
        Assert.True(engine.Query("phrase(seq([a]), [a]).").Success);
    }

    [Fact]
    public void UseModule_UnknownLibrary_Directive_WarnsButDoesNotAbort()
    {
        var engine = new PrologEngine();
        // As a directive, an unknown library must not abort the consult.
        engine.ConsultString(
            ":- use_module(library(no_such_library_xyz)).\n" +
            ":- public ok/0.\n" +
            "ok.\n");
        Assert.True(engine.Query("ok.").Success);
    }
}

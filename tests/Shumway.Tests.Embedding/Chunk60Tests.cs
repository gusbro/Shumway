using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 60: enforcement of the <c>:- discontiguous</c> and
/// <c>:- multifile</c> directives. Both used to be parsed and stored
/// in the module manifest but had no consult-time effect — clauses
/// could be scattered or duplicated across modules without diagnostics.
/// </summary>
public class Chunk60Tests
{
    // ============================================================================
    // Discontiguous enforcement
    // ============================================================================

    [Fact]
    public void Consult_ContiguousClauses_NoError()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public color/1.\n" +
            "color(red).\n" +
            "color(green).\n" +
            "color(blue).\n");
        Assert.Equal(3, engine.QueryAll("color(_).").Count());
    }

    [Fact]
    public void Consult_DiscontiguousWithoutDeclaration_Throws()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<System.InvalidOperationException>(() =>
            engine.ConsultString(
                ":- public color/1.\n" +
                ":- public size/1.\n" +
                "color(red).\n" +
                "size(small).\n" +
                "color(green).\n"));
        Assert.Contains("color/1", ex.Message);
        Assert.Contains("contiguous", ex.Message);
    }

    [Fact]
    public void Consult_DiscontiguousWithDeclaration_OK()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public color/1.\n" +
            ":- public size/1.\n" +
            ":- discontiguous color/1.\n" +
            "color(red).\n" +
            "size(small).\n" +
            "color(green).\n");
        Assert.Equal(2, engine.QueryAll("color(_).").Count());
    }

    // ============================================================================
    // Multifile enforcement
    // ============================================================================

    [Fact]
    public void Consult_SamePublicAcrossModules_WithoutMultifile_Throws()
    {
        // Two modules each declare and define greet/1 as public without
        // also declaring it :- multifile. The duplicate-public check
        // rejects the second module at query time (when the public
        // namespace is validated).
        var engine = new PrologEngine();
        engine.ConsultString(":- module(m1).\n:- public greet/1.\ngreet(hello).\n");
        engine.ConsultString(":- module(m2).\n:- public greet/1.\ngreet(world).\n");
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => engine.Query("greet(_)."));
        Assert.Contains("public", ex.Message);
        Assert.Contains("greet", ex.Message);
    }

    [Fact]
    public void Consult_SamePublicAcrossModules_WithMultifile_BothAccepted()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(colors_a).\n" +
            ":- public color/1.\n" +
            ":- multifile color/1.\n" +
            "color(red).\n");
        engine.ConsultString(
            ":- module(colors_b).\n" +
            ":- public color/1.\n" +
            ":- multifile color/1.\n" +
            "color(blue).\n");
        // Both modules' clauses are visible to query time.
        Assert.Equal(2, engine.QueryAll("color(_).").Count());
    }

    [Fact]
    public void Consult_PartialMultifile_StillThrows()
    {
        // Only one of the two modules declares multifile — the other
        // wins as the canonical owner, so the second module's public
        // clash still raises.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(m1).\n" +
            ":- public greet/1.\n" +
            ":- multifile greet/1.\n" +
            "greet(hi).\n");
        engine.ConsultString(
            ":- module(m2).\n" +
            ":- public greet/1.\n" +
            "greet(yo).\n");
        Assert.Throws<System.InvalidOperationException>(
            () => engine.Query("greet(_)."));
    }
}

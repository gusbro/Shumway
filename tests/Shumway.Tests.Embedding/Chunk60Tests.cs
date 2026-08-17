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
        engine.ConsultString("""
            :- public color/1.
            color(red).
            color(green).
            color(blue).
            """);
        Assert.Equal(3, engine.QueryAll("color(_).").Count());
    }

    [Fact]
    public void Consult_DiscontiguousWithoutDeclaration_Throws()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<System.InvalidOperationException>(() =>
            engine.ConsultString("""
                :- public color/1.
                :- public size/1.
                color(red).
                size(small).
                color(green).
                """));
        Assert.Contains("color/1", ex.Message);
        Assert.Contains("contiguous", ex.Message);
    }

    [Fact]
    public void Consult_DiscontiguousWithDeclaration_OK()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public color/1.
            :- public size/1.
            :- discontiguous color/1.
            color(red).
            size(small).
            color(green).
            """);
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
        engine.ConsultString("""
            :- module(m1).
            :- public greet/1.
            greet(hello).
            """);
        engine.ConsultString("""
            :- module(m2).
            :- public greet/1.
            greet(world).
            """);
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => engine.Query("greet(_)."));
        Assert.Contains("public", ex.Message);
        Assert.Contains("greet", ex.Message);
    }

    [Fact]
    public void Consult_SamePublicAcrossModules_WithMultifile_BothAccepted()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- module(colors_a).
            :- public color/1.
            :- multifile color/1.
            color(red).
            """);
        engine.ConsultString("""
            :- module(colors_b).
            :- public color/1.
            :- multifile color/1.
            color(blue).
            """);
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
        engine.ConsultString("""
            :- module(m1).
            :- public greet/1.
            :- multifile greet/1.
            greet(hi).
            """);
        engine.ConsultString("""
            :- module(m2).
            :- public greet/1.
            greet(yo).
            """);
        Assert.Throws<System.InvalidOperationException>(
            () => engine.Query("greet(_)."));
    }
}

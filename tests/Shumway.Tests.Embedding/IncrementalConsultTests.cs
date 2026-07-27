using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Incremental consult: a directive takes effect for every clause that follows it
/// in the same source, exactly as `:- op` already does. The clause stream is
/// consumed lazily so a `:- use_module` / `:- set_prolog_flag` executed mid-file
/// affects the parsing of subsequent clauses — the correct behaviour of any Prolog
/// engine, and what lets a source import a library and then use its operators.
/// </summary>
public class IncrementalConsultTests
{
    [Fact]
    public void UseModuleOperators_ApplyToLaterClausesInTheSameFile()
    {
        // `:- use_module(library(clpfd))` brings the `in`/`#>`/`..` operators; the
        // clause after it must parse with them. Before incremental consult the
        // whole file was parsed before the directive ran, so `X in 1..5` was a
        // syntax error.
        var e = new PrologEngine();
        e.ConsultString(
            ":- use_module(library(clpfd)).\n" +
            "solve(Xs) :- [X,Y] = Xs, X in 1..3, Y in 1..3, X #> Y, label([X,Y]).");
        Assert.True(e.Query("solve([X, Y]).").Success);
    }

    [Fact]
    public void SetPrologFlag_AppliesToLaterClausesInTheSameFile()
    {
        // double_quotes set mid-file changes how a later clause's "..." reads.
        var e = new PrologEngine();
        e.ConsultString(
            "before(X) :- X = \"ab\".\n" +
            ":- set_prolog_flag(double_quotes, chars).\n" +
            "after(X) :- X = \"ab\".");
        // `after` reads "ab" as a char list under the flag set just before it.
        Assert.Equal("[a,b]", RenderList(e.Query("after(X).")["X"]!));
    }

    [Fact]
    public void InFileTermExpansion_AppliesToLaterClausesInTheSameFile()
    {
        // A term_expansion defined in a file must expand that file's OWN later
        // clauses — the in-file case (clpz's `++>` grammar depends on it).
        var e = new PrologEngine();
        e.ConsultString(
            "term_expansion(macro(X), expanded(X)).\n" +
            "macro(hi).");
        Assert.True(e.Query("expanded(hi).").Success);
    }

    [Fact]
    public void InFileTermExpansion_IsOrderSensitive_LikeSwiAndScryer()
    {
        // Verified identical on SWI 9.2.3 and Scryer: a hook applies ONLY to
        // clauses AFTER its definition. A matching term before the definition
        // survives unexpanded.
        var e = new PrologEngine();
        e.ConsultString(
            "special(before).\n" +
            "term_expansion(special(X), handled(X)).\n" +
            "special(after).");
        Assert.True(e.Query("special(before).").Success);   // before def: not expanded
        Assert.False(e.Query("handled(before).").Success);  // never created
        Assert.False(e.Query("special(after).").Success);   // after def: expanded away
        Assert.True(e.Query("handled(after).").Success);     // its expansion
    }

    [Fact]
    public void InFileTermExpansion6_DualAccumulatorGrammar()
    {
        // clpz's shape: a term_expansion/6 whose body calls file-local helpers to
        // translate a custom `++>` grammar operator, defined and used in one file.
        var e = new PrologEngine();
        e.ConsultString(
            ":- op(1200, xfx, (++>)).\n" +
            "user:term_expansion((H ++> B), _, Ids, (H :- Body), [], [d|Ids]) :- \\+ member(d, Ids), mk(B, Body).\n" +
            "mk(G, call(G)).\n" +
            "greet ++> hello.\n" +
            "hello.");
        Assert.True(e.Query("greet.").Success);
    }

    [Fact]
    public void MultifileTermExpansionHook_BodyCallsAModuleLocalInsideOnce()
    {
        // clpz's exact shape: a `:- multifile user:term_expansion/6` whose body
        // calls a module-local helper inside once/1. A global hook declared
        // multifile must stay on the static pipeline (where MetaTransform lowers
        // once so the local mangles) — NOT be routed to the dynamic store, whose
        // pre-mangle leaves the once-nested call bare.
        var e = new PrologEngine();
        e.ConsultString(
            ":- op(1200, xfx, (++>)).\n" +
            ":- multifile user:term_expansion/6.\n" +
            "user:term_expansion((H ++> B), _, Ids, (H :- Body), [], [d|Ids]) :- " +
            "\\+ member(d, Ids), once(mk(B, Body)).\n" +
            "mk(G, call(G)).\n" +
            "greet ++> hello.\n" +
            "hello.");
        Assert.True(e.Query("greet.").Success);
    }

    [Fact]
    public void TermExpansionHook_WithPhraseDcgBody_UsableByALaterConsult()
    {
        // atts.pl's shape: a hook that builds its output via phrase of a local DCG.
        // Its own (top-level) re-expansion pass must not corrupt it.
        var e = new PrologEngine();
        e.ConsultString(
            ":- op(1150, fx, decl).\n" +
            "user:term_expansion((:- decl X), Clauses) :- phrase(gen(X), Clauses).\n" +
            "gen(X) --> [marked(X)].");
        e.ConsultString(":- decl hi.");
        Assert.True(e.Query("marked(hi).").Success);
    }

    [Fact]
    public void NestedLibraryWithHooks_DoesNotCorruptTheOuterConsult()
    {
        // The atts.pl regression: a library that both use_modules another library
        // AND defines a term_expansion hook. The nested dep's re-expansion runs a
        // sub-query mid-consult; it must not break the hook for a later consult.
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway-nested-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "helperlib.pl"),
                ":- module(helperlib, [help/1]).\nhelp(ok).");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "declib.pl"),
                ":- module(declib, []).\n" +
                ":- use_module(library(helperlib)).\n" +
                ":- op(1150, fx, decl).\n" +
                "user:term_expansion((:- decl X), Clauses) :- help(_), phrase(gen(X), Clauses).\n" +
                "gen(X) --> [marked(X)].");
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir);
            e.ConsultString(":- use_module(library(declib)).");   // loads helperlib nested
            e.ConsultString(":- decl hi.");
            Assert.True(e.Query("marked(hi).").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void OpDirective_StillAppliesToLaterClauses()
    {
        // The baseline the others generalise — a plain `:- op` from that point on.
        var e = new PrologEngine();
        e.ConsultString(
            ":- op(700, xfx, ===>).\n" +
            "rule(a ===> b).");
        Assert.True(e.Query("rule(a ===> b).").Success);
    }

    private static string RenderList(Shumway.Compiler.Ast.Term t)
    {
        var sb = new System.Text.StringBuilder("[");
        bool first = true;
        Shumway.Compiler.Ast.Term cur = t;
        while (cur is Shumway.Compiler.Ast.CompoundTerm { Functor: ".", Args: [var h, var tl] })
        {
            if (!first) sb.Append(',');
            sb.Append((h as Shumway.Compiler.Ast.AtomTerm)?.Name ?? h.ToString());
            first = false;
            cur = tl;
        }
        return sb.Append(']').ToString();
    }
}

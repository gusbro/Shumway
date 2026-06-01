using System.IO;
using System.Text;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// Chunk 257 — shared clause pretty-printer. Drives the
/// <c>portray_clause/1,2</c> builtins and the listing path
/// (chunk 254/256). Produces SWI-style output:
///
/// <list type="bullet">
/// <item>A fact (head only, or rule whose body is <c>true</c>)
///   prints on one line ending in a period.</item>
/// <item>A rule prints the head with a trailing <c> :-</c>,
///   then each conjunction goal on its own indented line,
///   ending in a period.</item>
/// <item>A DCG rule (<c>Head --> Body</c>) follows the same
///   layout with <c> --></c>.</item>
/// <item>A directive (<c>(:- Goal)</c>, one-arg compound) prints
///   on one line as <c>:- Goal.</c></item>
/// </list>
///
/// <para>Two AST transforms run before rendering:</para>
/// <list type="bullet">
/// <item><strong>Demangle</strong> — ModuleRewrite mangled every
///   local-predicate functor as <c>&lt;module&gt;$&lt;name&gt;</c>.
///   Strips the prefix so <c>user$helper(X)</c> reads as
///   <c>helper(X)</c>.</item>
/// <item><strong>Variable renaming</strong> — synthetic
///   <c>_G&lt;addr&gt;</c> names from a heap round-trip get
///   renumbered <c>A</c>, <c>B</c>, <c>C</c>, … (then
///   <c>A1</c>, <c>B1</c>, … after <c>Z</c>) sharing-aware:
///   the same source variable maps to the same letter wherever
///   it appears. User-given names (anything not starting with
///   <c>_G</c> or <c>_C</c>) pass through unchanged so a
///   consulted file's listing still shows <c>X</c>, <c>Y</c>,
///   <c>Acc</c>, … from the source.</item>
/// </list>
/// </summary>
public static class ClausePortrayer
{
    /// <summary>Prints <paramref name="clauseTerm"/> as a clause
    /// to <paramref name="output"/>, ending with a newline. The
    /// term is detected as fact / rule / DCG / directive from its
    /// shape (no out-of-band <c>ClauseKind</c> needed). Demangle
    /// + variable renaming applied automatically.</summary>
    public static void Print(TextWriter output, Term clauseTerm)
    {
        Term t = DemangleTerm(clauseTerm);
        t = RenameSyntheticVars(t);

        if (t is CompoundTerm rule && rule.Functor == ":-" && rule.Args.Length == 2)
        {
            Term head = rule.Args[0];
            Term body = rule.Args[1];
            if (body is AtomTerm at && at.Name == "true")
            {
                output.WriteLine(AstTermRenderer.Render(head) + ".");
                return;
            }
            output.WriteLine(AstTermRenderer.Render(head) + " :-");
            PrintBody(output, body);
            return;
        }
        if (t is CompoundTerm dcg && dcg.Functor == "-->" && dcg.Args.Length == 2)
        {
            output.WriteLine(AstTermRenderer.Render(dcg.Args[0]) + " -->");
            PrintBody(output, dcg.Args[1]);
            return;
        }
        if (t is CompoundTerm dir && dir.Functor == ":-" && dir.Args.Length == 1)
        {
            // 1199 keeps the body bounded below `:-`'s 1200 so a
            // (Goal1 ; Goal2) directive renders without an outer
            // paren wrap.
            output.WriteLine(":- " + AstTermRenderer.Render(dir.Args[0], 1199) + ".");
            return;
        }
        // Fact (atom, number, or non-`:-`/`--&gt;` compound).
        output.WriteLine(AstTermRenderer.Render(t) + ".");
    }

    private static void PrintBody(TextWriter output, Term body)
    {
        // Walk `,`-chains so each goal lands on its own indented
        // line; any other body shape prints on one indented line
        // and terminates the clause.
        if (body is CompoundTerm seq && seq.Functor == "," && seq.Args.Length == 2)
        {
            output.WriteLine("    " + AstTermRenderer.Render(seq.Args[0], 999) + ",");
            PrintBody(output, seq.Args[1]);
            return;
        }
        output.WriteLine("    " + AstTermRenderer.Render(body, 1200) + ".");
    }

    /// <summary>Recursively rewrites every <see cref="CompoundTerm.Functor"/>
    /// in the AST so listing output shows user-facing local
    /// predicate names (<c>user$helper</c> → <c>helper</c>)
    /// instead of the mangled forms <c>ModuleRewrite</c> stored.</summary>
    private static Term DemangleTerm(Term term)
    {
        if (term is not CompoundTerm c) return term;
        var newArgs = new Term[c.Args.Length];
        bool changed = false;
        for (int i = 0; i < c.Args.Length; i++)
        {
            var newArg = DemangleTerm(c.Args[i]);
            if (!ReferenceEquals(newArg, c.Args[i])) changed = true;
            newArgs[i] = newArg;
        }
        string newFunctor = PrologEngine.DemangleLocalName(c.Functor);
        if (!changed && newFunctor == c.Functor) return c;
        return new CompoundTerm(newFunctor, newArgs);
    }

    /// <summary>Walks the AST mapping each synthetic <c>_G&lt;n&gt;</c>
    /// / <c>_C&lt;n&gt;</c> variable name (the form
    /// <c>TermReader.Materialize</c> assigns to unbound heap cells)
    /// to a fresh single-letter name. Sharing-aware: the same
    /// synthetic name maps to the same letter throughout the
    /// term, so a clause like <c>foo(_G3) :- bar(_G3)</c> renders
    /// as <c>foo(A) :- bar(A)</c>. User-given names from a
    /// consulted source pass through unchanged.</summary>
    private static Term RenameSyntheticVars(Term term)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int next = 0;
        string FreshName()
        {
            int n = next++;
            int letter = n % 26;
            int suffix = n / 26;
            char c = (char)('A' + letter);
            return suffix == 0 ? c.ToString() : $"{c}{suffix}";
        }
        Term Rewrite(Term t)
        {
            switch (t)
            {
                case VarTerm v when IsSyntheticName(v.Name):
                    if (!map.TryGetValue(v.Name, out var nm))
                    {
                        nm = FreshName();
                        map[v.Name] = nm;
                    }
                    return new VarTerm(nm);
                case CompoundTerm c:
                    var newArgs = new Term[c.Args.Length];
                    bool changed = false;
                    for (int i = 0; i < c.Args.Length; i++)
                    {
                        newArgs[i] = Rewrite(c.Args[i]);
                        if (!ReferenceEquals(newArgs[i], c.Args[i])) changed = true;
                    }
                    return changed ? new CompoundTerm(c.Functor, newArgs) : c;
                default:
                    return t;
            }
        }
        return Rewrite(term);
    }

    private static bool IsSyntheticName(string name)
        => name.Length >= 2 && name[0] == '_'
            && (name[1] == 'G' || name[1] == 'C');
}

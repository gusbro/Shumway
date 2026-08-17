using System.IO;
using System.Text;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// clause pretty-printer with width-aware multi-line
/// layout. Drives the <c>portray_clause/1,2</c> builtins and the
/// listing path. Output matches the SWI / SICStus convention:
///
/// <list type="bullet">
/// <item>Fact (head only, or body == <c>true</c>) — one line,
///   trailing period.</item>
/// <item>Rule — head with trailing <c> :-</c>, each body goal on
///   its own line indented 4 spaces, period at the end of the
///   last goal.</item>
/// <item><strong>Any <c>,</c>-conjunction always breaks across
///   lines</strong>, paren-wrapped when it sits inside an argument:
///   <c>( g1, g2, ... )</c> with each goal aligned past the
///   opening <c>(</c>.</item>
/// <item>Once a compound has any sub-term that needs multi-line
///   (i.e. contains a <c>,</c>-chain anywhere recursively), its
///   args are placed each on their own line aligned past the
///   functor's opening <c>(</c>.</item>
/// <item>Anything else (atoms, numbers, vars, conjunction-free
///   compounds) renders inline through <see cref="AstTermRenderer"/>.</item>
/// </list>
///
/// <para>Two AST transforms run before layout:</para>
/// <list type="bullet">
/// <item><strong>Demangle</strong> — ModuleRewrite-mangled local
///   predicate names (<c>user$helper</c>) → source-spelled
///   (<c>helper</c>).</item>
/// <item><strong>Variable renaming</strong> — synthetic
///   <c>_G&lt;addr&gt;</c> names from a heap round-trip get
///   renumbered <c>A</c>, <c>B</c>, <c>C</c>, … sharing-aware.
///   Parser-given names pass through unchanged.</item>
/// </list>
/// </summary>
public static class ClausePortrayer
{
    /// <summary>Prints <paramref name="clauseTerm"/> as a clause to
    /// <paramref name="output"/>, ending with a terminating period
    /// and newline. The term's shape (fact / rule / DCG /
    /// directive) is detected from its structure.</summary>
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
                output.WriteLine(AstTermRenderer.RenderQuoted(head) + ".");
                return;
            }
            output.WriteLine(AstTermRenderer.RenderQuoted(head) + " :-");
            PrintBody(output, body, indent: 4);
            output.WriteLine(".");
            return;
        }
        if (t is CompoundTerm dcg && dcg.Functor == "-->" && dcg.Args.Length == 2)
        {
            output.WriteLine(AstTermRenderer.RenderQuoted(dcg.Args[0]) + " -->");
            PrintBody(output, dcg.Args[1], indent: 4);
            output.WriteLine(".");
            return;
        }
        if (t is CompoundTerm dir && dir.Functor == ":-" && dir.Args.Length == 1)
        {
            output.WriteLine(":- " + AstTermRenderer.RenderQuoted(dir.Args[0], 1199) + ".");
            return;
        }
        output.WriteLine(AstTermRenderer.RenderQuoted(t) + ".");
    }

    /// <summary>Prints the body of a rule. The top-level
    /// <c>,</c>-chain is always broken, one goal per line at
    /// <paramref name="indent"/>. Goals that themselves need
    /// multi-line layout (because they contain nested
    /// <c>,</c>-chains) recurse into the multi-line writer.</summary>
    private static void PrintBody(TextWriter w, Term body, int indent)
    {
        var goals = FlattenConjunction(body);
        for (int i = 0; i < goals.Count; i++)
        {
            w.Write(new string(' ', indent));
            WriteGoal(w, goals[i], indent);
            if (i < goals.Count - 1) w.WriteLine(",");
        }
    }

    /// <summary>Writes one goal at the current column
    /// <paramref name="indent"/>. Decides inline vs multi-line:
    /// goals with a <c>,</c>-chain anywhere inside force
    /// multi-line; everything else renders inline.</summary>
    private static void WriteGoal(TextWriter w, Term goal, int indent)
    {
        if (!NeedsMultiLine(goal))
        {
            w.Write(AstTermRenderer.RenderQuoted(goal, 999));
            return;
        }

        // A control construct with a ,-chain inside: the canonical functor
        // form `;(A, B)` is exactly what a reader must NOT see — portray in
        // the standard alternative layout, if-then-else conditions included.
        if (goal is CompoundTerm ctrl && ctrl.Args.Length == 2
            && (ctrl.Functor == ";" || ctrl.Functor == "->" || ctrl.Functor == "*->"))
        {
            WriteControl(w, ctrl, indent);
            return;
        }

        if (goal is CompoundTerm c && c.Functor == "," && c.Args.Length == 2)
        {
            // Paren-wrapped conjunction inside an argument:
            //   ( g1,
            //     g2,
            //     ...
            //   )
            // The opening `(` is at column `indent`; goals align at
            // `indent + 2` (past the `( `); the closing `)` returns
            // to column `indent`.
            var goals = FlattenConjunction(goal);
            w.Write("( ");
            for (int i = 0; i < goals.Count; i++)
            {
                if (i > 0) w.Write(new string(' ', indent + 2));
                WriteGoal(w, goals[i], indent + 2);
                if (i < goals.Count - 1) w.WriteLine(",");
                else w.WriteLine();
            }
            w.Write(new string(' ', indent));
            w.Write(")");
            return;
        }

        if (goal is CompoundTerm cc)
        {
            // Regular compound foo(arg1, arg2, ...) — break args
            // each on its own line, aligned past the open paren.
            string fname = Shumway.Builtins.TermRenderer.QuotedAtomName(cc.Functor);
            w.Write(fname);
            w.Write("(");
            int argIndent = indent + fname.Length + 1;
            for (int i = 0; i < cc.Args.Length; i++)
            {
                if (i > 0) w.Write(new string(' ', argIndent));
                WriteGoal(w, cc.Args[i], argIndent);
                if (i < cc.Args.Length - 1) w.WriteLine(",");
            }
            w.Write(")");
            return;
        }

        // Fallback (should not reach: NeedsMultiLine only returns
        // true for compounds containing `,`).
        w.Write(AstTermRenderer.RenderQuoted(goal, 999));
    }

    /// <summary>The standard alternative layout for a multi-line control
    /// construct — the <c>;</c>-chain flattened, one branch per
    /// <c>;</c>-aligned block, an if-then's condition on its own line:
    /// <code>
    /// (   Cond ->
    ///     Then
    /// ;   Else
    /// )
    /// </code></summary>
    private static void WriteControl(TextWriter w, CompoundTerm goal, int indent)
    {
        var branches = new List<Term>();
        Term cur = goal;
        while (cur is CompoundTerm { Functor: ";", Args.Length: 2 } semi)
        {
            branches.Add(semi.Args[0]);
            cur = semi.Args[1];
        }
        branches.Add(cur);
        w.Write("(   ");
        for (int i = 0; i < branches.Count; i++)
        {
            if (i > 0)
            {
                w.Write(new string(' ', indent));
                w.Write(";   ");
            }
            WriteBranch(w, branches[i], indent + 4);
            w.WriteLine();
        }
        w.Write(new string(' ', indent));
        w.Write(")");
    }

    /// <summary>One branch of the alternative layout, no trailing newline:
    /// an if-then splits at the arrow; a conjunction breaks one goal per
    /// line WITHOUT the extra paren wrap (the enclosing construct's
    /// delimiters already bracket it).</summary>
    private static void WriteBranch(TextWriter w, Term branch, int indent)
    {
        if (branch is CompoundTerm { Args.Length: 2 } ite
            && (ite.Functor == "->" || ite.Functor == "*->"))
        {
            WriteGoal(w, ite.Args[0], indent);
            w.WriteLine(" " + ite.Functor);
            w.Write(new string(' ', indent));
            WriteBranch(w, ite.Args[1], indent);
            return;
        }
        if (branch is CompoundTerm { Functor: ",", Args.Length: 2 })
        {
            var goals = FlattenConjunction(branch);
            for (int i = 0; i < goals.Count; i++)
            {
                if (i > 0) w.Write(new string(' ', indent));
                WriteGoal(w, goals[i], indent);
                if (i < goals.Count - 1) w.WriteLine(",");
            }
            return;
        }
        WriteGoal(w, branch, indent);
    }

    /// <summary>True when the goal's compact rendering would be
    /// misleading because it contains a <c>,</c>-chain — a
    /// "sequence of goals" that conceptually wants line breaks.
    /// Recursive: <c>foo(bar, (a, b))</c> needs multi-line
    /// because of the inner <c>(a, b)</c>.</summary>
    private static bool NeedsMultiLine(Term t)
    {
        if (t is not CompoundTerm c) return false;
        if (c.Functor == "," && c.Args.Length == 2) return true;
        foreach (var a in c.Args)
            if (NeedsMultiLine(a)) return true;
        return false;
    }

    /// <summary>Walks a right-associated <c>,</c>-chain
    /// (<c>(a, (b, (c, d)))</c>) into a flat list
    /// (<c>[a, b, c, d]</c>). Non-conjunction terms return as a
    /// single-element list.</summary>
    private static List<Term> FlattenConjunction(Term t)
    {
        var goals = new List<Term>();
        Walk(t);
        return goals;

        void Walk(Term n)
        {
            if (n is CompoundTerm c && c.Functor == "," && c.Args.Length == 2)
            {
                Walk(c.Args[0]);
                Walk(c.Args[1]);
            }
            else
            {
                goals.Add(n);
            }
        }
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
    /// / <c>_C&lt;n&gt;</c> variable name to a fresh single-letter
    /// name (A, B, C, …; then A1, B1, … after Z). Sharing-aware:
    /// same synthetic name maps to the same letter throughout.
    /// User-given names pass through unchanged.</summary>
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

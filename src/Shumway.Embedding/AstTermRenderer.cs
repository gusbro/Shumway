using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;

namespace Shumway.Embedding;

/// <summary>
/// operator-aware renderer for AST <see cref="Term"/>
/// trees. Replaces ad-hoc <see cref="object.ToString"/> calls that
/// produce canonical-form output (<c>=(X, hello(Y))</c>) with the
/// reader-friendly operator form (<c>X = hello(Y)</c>), list syntax
/// (<c>[a, b | T]</c> instead of <c>.(a, .(b, T))</c>), and the
/// standard atom / number / string spellings.
///
/// <para>Originally inlined inside <see cref="Solution"/>'s
/// <c>Render</c> for binding display; lifted out so the
/// <c>listing</c> path and any future AST consumer
/// can reuse the same logic. The renderer operates on the AST
/// <em>without</em> involving the engine heap, so variable names
/// the parser captured (<see cref="VarTerm.Name"/>) survive
/// verbatim — the original goal of the chunk.</para>
/// </summary>
public static class AstTermRenderer
{
    private static readonly OperatorTable DefaultOps = OperatorTable.Default();

    /// <summary>Renders <paramref name="term"/> using the default
    /// operator table (the one the parser uses), at the maximum
    /// priority bound — i.e. no enclosing context forces
    /// parenthesisation. Equivalent to Prolog's
    /// <c>write_term(Term, [quoted(false)])</c> rendering.</summary>
    public static string Render(Term term)
        => Render(term, 1200, DefaultOps);

    /// <summary>Renders <paramref name="term"/> bounded by
    /// <paramref name="maxPrec"/>: a compound whose operator priority
    /// exceeds the bound is wrapped in parens so the result re-parses
    /// to the same AST shape.</summary>
    public static string Render(Term term, int maxPrec)
        => Render(term, maxPrec, DefaultOps);

    /// <summary>The <c>writeq</c>-flavoured render with the default operator
    /// table — what <c>listing</c>/<c>portray_clause</c> emit, where an atom
    /// that would not re-read as itself must be quoted.</summary>
    public static string RenderQuoted(Term term, int maxPrec = 1200)
        => Render(term, maxPrec, DefaultOps, quoted: true);

    /// <summary>Operator-aware overload using a caller-supplied table —
    /// pass <see cref="PrologEngine.Operators"/> to render terms that
    /// mention operators introduced at runtime (e.g. CLP(FD)'s
    /// <c>in</c>, <c>..</c>, <c>#=</c>) in their operator form.</summary>
    public static string Render(Term term, int maxPrec, OperatorTable ops)
        => Render(term, maxPrec, ops, quoted: false);

    /// <summary>ADR-035 D5+ — the <c>writeq</c>-style overload: atoms and canonical
    /// functor names that would not re-parse to the same term are single-quoted. The
    /// debugger's displays use this — a Locals value feeds the Watch-window EDIT, and
    /// showing the atom <c>'1234'</c> as bare <c>1234</c> made the round-tripped value
    /// an INTEGER. Operator occurrences stay unquoted (they re-parse as written).</summary>
    public static string Render(Term term, int maxPrec, OperatorTable ops, bool quoted)
        => Render(term, maxPrec, ops, quoted, portrayText: false);

    /// <summary>The top level's ANSWER rendering: quoted (re-readable — a raw
    /// newline inside an atom never leaks into the transcript) and with text
    /// portrayed: a proper list of characters shows as <c>"..."</c> with
    /// escapes. Program text (listing, portray_clause) stays list-shaped;
    /// this form is for a human reading answers.</summary>
    public static string RenderAnswer(Term term, OperatorTable ops)
        => Render(term, 1200, ops, quoted: true, portrayText: true);

    /// <summary>Renders a term on an EXPLICIT stack. How deep a term nests is
    /// the program's choice, so a recursive renderer spends a C# frame per
    /// level, and a .NET stack overflow cannot be caught: it takes the process
    /// down with nothing to report.
    ///
    /// <para>The shape is post-order because the spelling of a node depends on
    /// the TEXT of its children: a tight symbolic operator has to know whether
    /// its operand ends in a graphic char, or <c>X = -1</c> would come back as
    /// <c>X=-1</c> and lex <c>=-</c> as one token. So children render onto a
    /// results stack and each node assembles its own text from them. What each
    /// node does with them is unchanged; only the descent moved.</para>
    /// </summary>
    public static string Render(
        Term term, int maxPrec, OperatorTable ops, bool quoted, bool portrayText)
    {
        var work = new Stack<Step>();
        var done = new Stack<string>();
        work.Push(Step.Descend(term, maxPrec, asOperand: false));
        while (work.Count > 0)
        {
            Step step = work.Pop();
            if (step.Kind == Shape.Descend)
                Push(step, work, done, ops, quoted, portrayText);
            else
                done.Push(Assemble(step, done, ops, quoted));
        }
        return done.Pop();
    }

    /// <summary>What a node still owes once its children are rendered.</summary>
    private enum Shape { Descend, Infix, Prefix, Postfix, Canonical, Curly, List, OpenText }

    private readonly record struct Step(
        Shape Kind, Term Node, int MaxPrec, bool AsOperand, int Count, string Text)
    {
        public static Step Descend(Term t, int maxPrec, bool asOperand)
            => new(Shape.Descend, t, maxPrec, asOperand, 0, "");

        public static Step Assembling(
            Shape kind, Term node, int maxPrec, int count, string text = "")
            => new(kind, node, maxPrec, false, count, text);
    }

    /// <summary>Decides what a node is and queues the work: either its finished
    /// text straight onto the results, or an assembly step underneath the
    /// children it needs. Children go on in REVERSE so they render left to
    /// right and land on the results stack in that order.</summary>
    private static void Push(
        Step step, Stack<Step> work, Stack<string> done,
        OperatorTable ops, bool quoted, bool portrayText)
    {
        Term term = step.Node;
        // An OPERAND that is a bare operator atom would not re-read (ISO
        // 6.3.1.3, the s#378 rule the parser now enforces), so it renders
        // parenthesised — `(is)/2`, never `is/2`. Argument and list positions
        // keep the bare atom, which is exactly where ISO admits it. Decided
        // from the TERM, so it is settled here rather than after rendering.
        if (step.AsOperand && term is AtomTerm operandAtom
            && IsOperatorAtom(operandAtom.Name, ops))
        {
            done.Push("(" + AtomText(operandAtom.Name, quoted) + ")");
            return;
        }
        switch (term)
        {
            case AtomTerm a: done.Push(AtomText(a.Name, quoted)); return;
            case VarTerm v: done.Push(v.Name); return;
            case IntTerm n: done.Push(n.Value.ToString(CultureInfo.InvariantCulture)); return;
            case FloatTerm f:
                done.Push(Shumway.Builtins.Number.FormatPrologFloat(f.Value)); return;
            case StringTerm s: done.Push(RenderDoubleQuoted(s.Content)); return;
            case BigIntTerm b:
                done.Push(b.Value.ToString(CultureInfo.InvariantCulture)); return;

            case CompoundTerm { Functor: ".", Args.Length: 2 } list:
            {
                if (portrayText && TryRenderTextList(list, out string text))
                { done.Push(text); return; }
                if (portrayText && TryOpenTextPrefix(list, out string prefix, out Term openTail))
                {
                    work.Push(Step.Assembling(Shape.OpenText, list, 0, 1, prefix));
                    work.Push(Step.Descend(openTail, 0, asOperand: false));
                    return;
                }
                // The spine is walked HERE, so a long list costs no depth at
                // all; only its elements nest.
                var elements = new List<Term>();
                Term cursor = list;
                while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
                {
                    elements.Add(cons.Args[0]);
                    cursor = cons.Args[1];
                }
                bool proper = cursor is AtomTerm { Name: "[]" };
                work.Push(Step.Assembling(Shape.List, list, 0,
                    elements.Count + (proper ? 0 : 1), proper ? "]" : "|"));
                if (!proper) work.Push(Step.Descend(cursor, 999, asOperand: false));
                for (int i = elements.Count - 1; i >= 0; i--)
                    work.Push(Step.Descend(elements[i], 999, asOperand: false));
                return;
            }

            // '{}'(X) reads back as {X} — the canonical form would re-parse
            // but is not what writeq/portray_clause emit.
            case CompoundTerm { Functor: "{}", Args.Length: 1 } curly:
                work.Push(Step.Assembling(Shape.Curly, curly, step.MaxPrec, 1));
                work.Push(Step.Descend(curly.Args[0], 1200, asOperand: false));
                return;

            case CompoundTerm c when c.Args.Length == 2
                    && ops.TryGetInfix(c.Functor, out int iPrec, out var iType):
                work.Push(Step.Assembling(Shape.Infix, c, step.MaxPrec, 2));
                work.Push(Step.Descend(c.Args[1],
                    iType == OperatorType.Xfy ? iPrec : iPrec - 1, asOperand: true));
                work.Push(Step.Descend(c.Args[0],
                    iType == OperatorType.Yfx ? iPrec : iPrec - 1, asOperand: true));
                return;

            case CompoundTerm c when c.Args.Length == 1
                    && ops.TryGetPrefix(c.Functor, out int pPrec, out var pType):
                work.Push(Step.Assembling(Shape.Prefix, c, step.MaxPrec, 1));
                work.Push(Step.Descend(c.Args[0],
                    pType == OperatorType.Fy ? pPrec : pPrec - 1, asOperand: true));
                return;

            case CompoundTerm c when c.Args.Length == 1
                    && ops.TryGetPostfix(c.Functor, out int sPrec, out var sType):
                work.Push(Step.Assembling(Shape.Postfix, c, step.MaxPrec, 1));
                work.Push(Step.Descend(c.Args[0],
                    sType == OperatorType.Yf ? sPrec : sPrec - 1, asOperand: true));
                return;

            case CompoundTerm c:
                work.Push(Step.Assembling(Shape.Canonical, c, step.MaxPrec, c.Args.Length));
                for (int i = c.Args.Length - 1; i >= 0; i--)
                    work.Push(Step.Descend(c.Args[i], 999, asOperand: false));
                return;

            default:
                done.Push(term.ToString() ?? "?");
                return;
        }
    }

    /// <summary>Builds a node's text from the children already on the results
    /// stack. Byte for byte what the recursive form composed.</summary>
    private static string Assemble(
        Step step, Stack<string> done, OperatorTable ops, bool quoted)
    {
        var parts = new string[step.Count];
        for (int i = step.Count - 1; i >= 0; i--) parts[i] = done.Pop();
        var c = (CompoundTerm)step.Node;
        switch (step.Kind)
        {
            case Shape.OpenText:
                return step.Text + "||" + parts[0];

            case Shape.List:
                return step.Text == "]"
                    ? "[" + string.Join(", ", parts) + "]"
                    : "[" + string.Join(", ", parts, 0, parts.Length - 1)
                          + " | " + parts[^1] + "]";

            case Shape.Curly:
                return "{" + parts[0] + "}";

            case Shape.Infix:
            {
                ops.TryGetInfix(c.Functor, out int iPrec, out _);
                // comma and semicolon (sequence / disjunction operators)
                // render with no leading space: `a, b` and `a ; b`. Symbolic
                // operators (`+`, `/`, `=`) stay tight. Alphabetic operators
                // (`is`, `mod`) keep spaces both sides.
                string sep = c.Functor switch
                {
                    "," => ", ",
                    ";" => "; ",
                    _ when IsSymbolic(c.Functor) => c.Functor,
                    _ => $" {c.Functor} ",
                };
                string leftStr = parts[0], rightStr = parts[1];
                // A tight symbolic operator fuses with a graphic-ending operand
                // into ONE token on re-read (`.. = ..` as `..=..`; `X = -1` as
                // `X=-1`, lexing `=-`): pad exactly where adjacency would fuse.
                if (sep.Length > 0 && IsGraphicChar(sep[0]))
                {
                    if (leftStr.Length > 0 && IsGraphicChar(leftStr[^1]))
                        sep = " " + sep;
                    if (rightStr.Length > 0 && IsGraphicChar(rightStr[0]))
                        sep += " ";
                }
                string infixBody = $"{leftStr}{sep}{rightStr}";
                return iPrec > step.MaxPrec ? $"({infixBody})" : infixBody;
            }

            case Shape.Prefix:
            {
                ops.TryGetPrefix(c.Functor, out int pPrec, out _);
                string prefixBody = $"{c.Functor} {parts[0]}";
                return pPrec > step.MaxPrec ? $"({prefixBody})" : prefixBody;
            }

            case Shape.Postfix:
            {
                ops.TryGetPostfix(c.Functor, out int sPrec, out _);
                string sep = IsSymbolic(c.Functor) ? c.Functor : $" {c.Functor}";
                string operandStr = parts[0];
                if (sep.Length > 0 && IsGraphicChar(sep[0])
                    && operandStr.Length > 0 && IsGraphicChar(operandStr[^1]))
                    sep = " " + sep;
                string postfixBody = $"{operandStr}{sep}";
                return sPrec > step.MaxPrec ? $"({postfixBody})" : postfixBody;
            }

            default:
            {
                // Canonical form. Arguments sit at priority 999 (below the
                // argument-comma's 1000) so a comma-term arg gets parenthesised.
                var sb = new StringBuilder(AtomText(c.Functor, quoted));
                sb.Append('(');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(parts[i]);
                }
                return sb.Append(')').ToString();
            }
        }
    }

    private static string AtomText(string name, bool quoted)
        => quoted ? Shumway.Builtins.TermRenderer.QuotedAtomName(name) : name;

    private static bool IsOperatorAtom(string name, OperatorTable ops)
        => ops.TryGetInfix(name, out _, out _)
           || ops.TryGetPrefix(name, out _, out _)
           || ops.TryGetPostfix(name, out _, out _);

    /// <summary>A proper, non-empty list of single-character atoms renders as
    /// a double-quoted string — the text reading of the default
    /// <c>double_quotes = chars</c>. CODES stay numeric on purpose:
    /// <c>[65, 66]</c> displaying as <c>"AB"</c> would dress arbitrary small
    /// integers up as text (the strictest engine agrees); the cell writer's
    /// portray-text OPTION still covers codes for callers that ask.</summary>
    private static bool TryRenderTextList(CompoundTerm cons, out string rendered)
    {
        rendered = "";
        var sb = new StringBuilder();
        Term cursor = cons;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            if (c.Args[0] is not AtomTerm a || !IsOneCodePoint(a.Name))
                return false;
            sb.Append(a.Name);
            cursor = c.Args[1];
        }
        if (cursor is not AtomTerm { Name: "[]" } || sb.Length == 0) return false;
        rendered = RenderDoubleQuoted(sb.ToString());
        return true;
    }

    /// <summary>The same text reading for a list left OPEN, written the way
    /// it can be read back: `"abc"||T`. An answer to a grammar is a difference
    /// list, and spelling out each of its characters buries the one thing the
    /// reader is after, which is where the text ends and the tail begins.
    ///
    /// <para>Returns the text PREFIX and the tail still to render; the tail
    /// goes through the ordinary descent so it costs no C# depth.</para>
    /// </summary>
    private static bool TryOpenTextPrefix(
        CompoundTerm cons, out string prefix, out Term tail)
    {
        prefix = "";
        tail = cons;
        var sb = new StringBuilder();
        Term cursor = cons;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            if (c.Args[0] is not AtomTerm a || !IsOneCodePoint(a.Name)) return false;
            sb.Append(a.Name);
            cursor = c.Args[1];
        }
        // A closed one is the other reading's business, and an empty prefix
        // prepends nothing: `""||T` is T.
        if (cursor is AtomTerm { Name: "[]" } || sb.Length == 0) return false;
        prefix = RenderDoubleQuoted(sb.ToString());
        tail = cursor;
        return true;
    }

    private static bool IsOneCodePoint(string name) =>
        name.Length == 1
        || (name.Length == 2 && char.IsHighSurrogate(name[0])
            && char.IsLowSurrogate(name[1]));

    /// <summary>Double-quoted text with the writeq escapes — a raw control
    /// character in the content must never reach the transcript raw.</summary>
    private static string RenderDoubleQuoted(string content)
    {
        var sb = new StringBuilder(content.Length + 2);
        sb.Append('"');
        foreach (char ch in content)
        {
            if (ch == '"') { sb.Append("\\\""); continue; }
            string? esc = Shumway.Builtins.TermRenderer.EscapeQuotedChar(ch);
            // The single quote is literal inside double quotes.
            if (esc is not null && ch != '\'') sb.Append(esc);
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    private static bool IsSymbolic(string name)
    {
        if (name.Length == 0) return false;
        foreach (char ch in name)
            if (!IsGraphicChar(ch)) return false;
        return true;
    }

    /// <summary>A char of the Prolog graphic-token alphabet: two adjacent
    /// graphic chars lex as one token, so renderers must pad where rendered
    /// pieces would otherwise fuse.</summary>
    internal static bool IsGraphicChar(char ch)
        => "+-*/\\^<>=~:.?@#&$".IndexOf(ch) >= 0;
}

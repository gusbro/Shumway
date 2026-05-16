using Shumway.Compiler.Lexer;

namespace Shumway.Compiler.Ast;

/// <summary>
/// Coarse classification of a top-level clause in a Prolog source file. The
/// distinction is purely structural — every shape is just a particular kind of
/// <see cref="Term"/> — but downstream stages handle each class differently:
/// directives are executed at load time, rules and facts are added to the
/// predicate table, and DCG rules are rewritten into ordinary rules.
/// </summary>
public enum ClauseKind
{
    /// <summary>A bare term other than the special structural compounds below.
    /// Examples: <c>foo.</c>, <c>p(a, 1).</c>.</summary>
    Fact,

    /// <summary>A rule, encoded as <c>:-/2</c>: head and body.</summary>
    Rule,

    /// <summary>A directive, encoded as <c>:-/1</c> (the prefix form).
    /// Executed at load time — <c>:- op(...)</c>, <c>:- dynamic ...</c>,
    /// <c>:- public ...</c>, etc.</summary>
    Directive,

    /// <summary>A DCG rule, encoded as <c>--&gt;/2</c>. Rewritten to a normal rule
    /// by the DCG-translation pass; carried through as a separate kind until
    /// then.</summary>
    DcgRule,
}

/// <summary>
/// A single parsed top-level clause: the raw term plus its <see cref="ClauseKind"/>
/// classification and source position. The wrapper is intentionally thin — the
/// underlying <see cref="Term"/> retains all structural information, including
/// <c>:-/2</c> heads/bodies, so downstream stages don't need a separate "head term"
/// / "body term" split here.
/// </summary>
public sealed class Clause
{
    public ClauseKind Kind { get; }
    public Term Term { get; }
    public SourcePosition Position { get; }

    public Clause(ClauseKind kind, Term term, SourcePosition position)
    {
        Kind = kind;
        Term = term;
        Position = position;
    }

    /// <summary>Wraps <paramref name="term"/> with its inferred <see cref="ClauseKind"/>
    /// and position.</summary>
    public static Clause From(Term term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return new Clause(Classify(term), term, term.Position);
    }

    /// <summary>Returns the <see cref="ClauseKind"/> a term would have at the top
    /// level. <c>:-/2</c> is a rule; <c>:-/1</c> a directive; <c>--&gt;/2</c> a DCG
    /// rule; anything else is a fact.</summary>
    public static ClauseKind Classify(Term term)
    {
        if (term is CompoundTerm ct)
        {
            return (ct.Functor, ct.Args.Length) switch
            {
                (":-", 2) => ClauseKind.Rule,
                (":-", 1) => ClauseKind.Directive,
                ("-->", 2) => ClauseKind.DcgRule,
                _ => ClauseKind.Fact,
            };
        }
        return ClauseKind.Fact;
    }

    public override string ToString() => $"{Kind}: {Term}";
}

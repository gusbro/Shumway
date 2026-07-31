namespace Shumway.Compiler.Parsing;

/// <summary>
/// Mutable registry of Prolog operators. Each operator is keyed by name and kind
/// (prefix, infix, postfix); the same atom can have multiple definitions across
/// different kinds — <c>-</c> is both a unary prefix and a binary infix, for
/// example. <see cref="Default"/> returns a freshly-built table seeded with the
/// the ISO operators grammar-processing code most commonly uses.
///
/// <para>The parser consults the table at every potential operator position to
/// decide whether the current atom is acting as a prefix, infix or postfix
/// operator (or just as a plain atom). The <c>:- op(P, T, N)</c> directive will
/// later mutate the table at parse time.</para>
/// </summary>
public sealed class OperatorTable
{
    private readonly Dictionary<string, OperatorInfo> _byName = new();

    /// <summary>Registers an operator. Replaces any prior definition of the same
    /// (name, fixity) pair. Use <c>0</c> as the precedence to remove an
    /// existing entry, matching the standard <c>:- op</c> behaviour.</summary>
    public void Define(string name, int precedence, OperatorType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (precedence < 0 || precedence > 1200)
            throw new ArgumentOutOfRangeException(nameof(precedence),
                $"Operator precedence must be in [0, 1200]; was {precedence}.");

        if (!_byName.TryGetValue(name, out var info))
        {
            if (precedence == 0) return;
            info = new OperatorInfo();
            _byName[name] = info;
        }

        if (type.IsPrefix())
        {
            info.PrefixPrecedence = precedence == 0 ? null : precedence;
            info.PrefixType = type;
        }
        else if (type.IsInfix())
        {
            info.InfixPrecedence = precedence == 0 ? null : precedence;
            info.InfixType = type;
        }
        else // postfix
        {
            info.PostfixPrecedence = precedence == 0 ? null : precedence;
            info.PostfixType = type;
        }

        if (info.IsEmpty) _byName.Remove(name);
    }

    public bool TryGetPrefix(string name, out int precedence, out OperatorType type)
    {
        if (_byName.TryGetValue(name, out var info) && info.PrefixPrecedence is int p)
        {
            precedence = p;
            type = info.PrefixType;
            return true;
        }
        precedence = 0;
        type = default;
        return false;
    }

    public bool TryGetInfix(string name, out int precedence, out OperatorType type)
    {
        if (_byName.TryGetValue(name, out var info) && info.InfixPrecedence is int p)
        {
            precedence = p;
            type = info.InfixType;
            return true;
        }
        precedence = 0;
        type = default;
        return false;
    }

    public bool TryGetPostfix(string name, out int precedence, out OperatorType type)
    {
        if (_byName.TryGetValue(name, out var info) && info.PostfixPrecedence is int p)
        {
            precedence = p;
            type = info.PostfixType;
            return true;
        }
        precedence = 0;
        type = default;
        return false;
    }

    /// <summary>Enumerates every (priority, type, name) currently
    /// registered. Each (name, fixity) pair produces one entry, so an
    /// atom that's both prefix and infix (e.g. <c>-</c>) appears
    /// twice. Used by <c>current_op/3</c> (ISO §8.17.3) to drive its
    /// backtracking enumeration.</summary>
    public IEnumerable<(int Precedence, OperatorType Type, string Name)> Enumerate()
    {
        foreach (var (name, info) in _byName)
        {
            if (info.PrefixPrecedence is int pp)
                yield return (pp, info.PrefixType, name);
            if (info.InfixPrecedence is int ip)
                yield return (ip, info.InfixType, name);
            if (info.PostfixPrecedence is int ppx)
                yield return (ppx, info.PostfixType, name);
        }
    }

    /// <summary>Returns a fresh table seeded with the operators common to ISO and
    /// SWI-style Prolog. Edits to the returned table do not affect future
    /// invocations.</summary>
    public static OperatorTable Default()
    {
        var t = new OperatorTable();

        // Clause / directive
        t.Define(":-", 1200, OperatorType.Xfx);
        t.Define("-->", 1200, OperatorType.Xfx);
        t.Define(":-", 1200, OperatorType.Fx);
        t.Define("?-", 1200, OperatorType.Fx);

        // Module-level directive heads (common public/dynamic-style declarations)
        t.Define("public", 1150, OperatorType.Fx);
        t.Define("dynamic", 1150, OperatorType.Fx);
        // Arity-Prolog alias for `dynamic`. Accepted at the
        // same precedence so `:- visible foo/N.` parses identically to
        // `:- dynamic foo/N.`; the directive handler treats them as
        // synonyms.
        t.Define("visible", 1150, OperatorType.Fx);
        t.Define("discontiguous", 1150, OperatorType.Fx);
        t.Define("multifile", 1150, OperatorType.Fx);
        t.Define("module_transparent", 1150, OperatorType.Fx);
        t.Define("volatile", 1150, OperatorType.Fx);
        // SWI/SICStus `:- meta_predicate foo(0,?), bar(:,+).` — the prefix
        // operator so it parses (the directive is no-op'd at consult time).
        // Without it, a library declaring meta_predicate before any op-defining
        // load (SWI's library(assoc), …) fails to parse. ADR-040.
        t.Define("meta_predicate", 1150, OperatorType.Fx);
        // Scryer directive — marks a predicate as not counting toward inference
        // limits. Shumway has no inference-limit machinery, so it is a pure no-op;
        // the operator exists only so `:- non_counted_backtracking foo/N.` parses
        // (e.g. loading Scryer's library(iso_ext)).
        t.Define("non_counted_backtracking", 1150, OperatorType.Fx);
        t.Define("table", 1150, OperatorType.Fx);
        t.Define("mode", 1150, OperatorType.Fx);
        t.Define("ensure_linked", 1150, OperatorType.Fx);
        t.Define("native", 1150, OperatorType.Fx);   // ADR-024 — `:- native fn/N`
        // SWI/ISO-style load-time goal directive: `:- initialization main.`
        // parses without parens (SWI declares the same fx 1150 operator).
        t.Define("initialization", 1150, OperatorType.Fx);

        // Control
        t.Define(";", 1100, OperatorType.Xfy);
        t.Define("|", 1100, OperatorType.Xfy);
        t.Define("->", 1050, OperatorType.Xfy);
        t.Define("*->", 1050, OperatorType.Xfy);
        t.Define(",", 1000, OperatorType.Xfy);

        // Negation
        t.Define("\\+", 900, OperatorType.Fy);
        t.Define("not", 900, OperatorType.Fy);

        // Comparison and unification
        t.Define("=", 700, OperatorType.Xfx);
        t.Define("\\=", 700, OperatorType.Xfx);
        t.Define("==", 700, OperatorType.Xfx);
        t.Define("\\==", 700, OperatorType.Xfx);
        t.Define("@<", 700, OperatorType.Xfx);
        t.Define("@>", 700, OperatorType.Xfx);
        t.Define("@=<", 700, OperatorType.Xfx);
        t.Define("@>=", 700, OperatorType.Xfx);
        t.Define("=..", 700, OperatorType.Xfx);
        t.Define("is", 700, OperatorType.Xfx);
        t.Define("=:=", 700, OperatorType.Xfx);
        t.Define("=\\=", 700, OperatorType.Xfx);
        t.Define("<", 700, OperatorType.Xfx);
        t.Define(">", 700, OperatorType.Xfx);
        t.Define("=<", 700, OperatorType.Xfx);
        t.Define(">=", 700, OperatorType.Xfx);
        t.Define("?=", 700, OperatorType.Xfx);

        // Arithmetic
        t.Define("+", 500, OperatorType.Yfx);
        t.Define("-", 500, OperatorType.Yfx);
        t.Define("/\\", 500, OperatorType.Yfx);
        t.Define("\\/", 500, OperatorType.Yfx);
        t.Define("xor", 500, OperatorType.Yfx);

        t.Define("*", 400, OperatorType.Yfx);
        t.Define("/", 400, OperatorType.Yfx);
        t.Define("//", 400, OperatorType.Yfx);
        t.Define("rdiv", 400, OperatorType.Yfx);   // exact rational division (ADR-039)
        t.Define("mod", 400, OperatorType.Yfx);
        t.Define("rem", 400, OperatorType.Yfx);
        t.Define("div", 400, OperatorType.Yfx);
        t.Define("<<", 400, OperatorType.Yfx);
        t.Define(">>", 400, OperatorType.Yfx);

        t.Define("**", 200, OperatorType.Xfx);
        t.Define("^", 200, OperatorType.Xfy);

        // Unary
        t.Define("-", 200, OperatorType.Fy);
        t.Define("+", 200, OperatorType.Fy);
        t.Define("\\", 200, OperatorType.Fy);

        // Tag / qualifier — usually module qualification
        t.Define(":", 200, OperatorType.Xfy);

        return t;
    }

    private sealed class OperatorInfo
    {
        public int? PrefixPrecedence;
        public OperatorType PrefixType;
        public int? InfixPrecedence;
        public OperatorType InfixType;
        public int? PostfixPrecedence;
        public OperatorType PostfixType;

        public bool IsEmpty =>
            PrefixPrecedence is null && InfixPrecedence is null && PostfixPrecedence is null;
    }
}

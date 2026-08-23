using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Single-sided-unification (SSU) rules — a first-class clause form of the
/// engine, alongside <c>:-</c> and DCG <c>--&gt;</c>. An SSU rule is written
/// <c>Head =&gt; Body</c> (committed choice) or <c>Head, Guard =&gt; Body</c>
/// (guarded), with SWI's semantics — the only mainstream Prolog that
/// implements <c>=&gt;</c>, so its behaviour is the reference:
///
/// <list type="bullet">
///   <item>The head is a PATTERN, matched single-sidedly: bindings flow from
///   the goal into the head's variables only — a match that would have to
///   bind a variable of the CALLER's goal does not apply, and the next rule
///   is tried.</item>
///   <item>Once a head matches (and its guard, if any, succeeds) the rule
///   commits: no other clause is tried.</item>
///   <item>When NO rule matches, the call raises
///   <c>existence_error(matching_rule, Goal)</c> instead of failing.</item>
/// </list>
///
/// <para>Lowering (each rule, plus one synthesized trailer per predicate):</para>
/// <list type="bullet">
///   <item><c>p(T…) =&gt; Body</c> → <c>p(V…) :- '$ssu_match'(p(T…), p(V…)), !, Body</c></item>
///   <item><c>(p(T…), Guard) =&gt; Body</c> → <c>p(V…) :- '$ssu_match'(p(T…), p(V…)), Guard, !, Body</c></item>
///   <item>trailer: <c>p(V…) :- '$ssu_no_match'(p(V…))</c> — reached only when
///   every rule's match/guard refused, raising the SWI error.</item>
/// </list>
///
/// <para><c>'$ssu_match'(P, G)</c> is the prelude's
/// <c>subsumes_term(P, G), P = G</c>: the subsumption check is the
/// single-sidedness (only pattern variables may bind), the unification then
/// binds them for the guard/body. The all-variables lowered head costs SSU
/// predicates first-argument indexing — the price of source-level lowering;
/// engine-level one-sided head instructions are the future upgrade path.</para>
///
/// <para>The no-match trailer is emitted per contiguous clause run that
/// contained at least one <c>=&gt;</c> rule (contiguity is enforced by the
/// consult, so a predicate is one run). Asserting <c>=&gt;</c> clauses one at
/// a time into the same predicate emits one trailer per assert — a known
/// limitation; SWI's SSU predicates are static in practice.</para>
/// </summary>
public static class SsuTransform
{
    public static IEnumerable<Clause> Apply(IEnumerable<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        return ApplyCore(clauses);
    }

    private static IEnumerable<Clause> ApplyCore(IEnumerable<Clause> clauses)
    {
        // Current contiguous head run, for the no-match trailer.
        string? runName = null;
        int runArity = -1;
        bool runHadSsu = false;
        Lexer.SourcePosition runPos = default;

        foreach (var c in clauses)
        {
            var key = HeadKeyOf(c);
            if (key is { } k && (k.Name != runName || k.Arity != runArity))
            {
                if (runHadSsu)
                    yield return NoMatchTrailer(runName!, runArity, runPos);
                runName = k.Name;
                runArity = k.Arity;
                runHadSsu = false;
            }

            if (c.Term is CompoundTerm { Functor: "=>", Args: [var lhs, var body] })
            {
                runHadSsu = true;
                runPos = c.Term.Position;
                yield return Clause.From(Rewrite(lhs, body, c.Term.Position));
            }
            else
            {
                yield return c;
            }
        }
        if (runHadSsu)
            yield return NoMatchTrailer(runName!, runArity, runPos);
    }

    /// <summary>The defining predicate's name/arity — the grouping key for
    /// the no-match trailer. Null for directives and for shapes the SSU
    /// lowering leaves alone (qualified heads), which then neither start nor
    /// break a run.</summary>
    private static (string Name, int Arity)? HeadKeyOf(Clause c)
    {
        Term t = c.Term;
        if (t is CompoundTerm { Functor: ":-", Args.Length: 1 }) return null;   // directive
        if (t is CompoundTerm { Functor: ":-" or "-->" or "=>", Args: [var l, _] })
        {
            t = l;
            // SSU guard form: the head is the first conjunct.
            if (c.Term is CompoundTerm { Functor: "=>" }
                && t is CompoundTerm { Functor: ",", Args: [var h, _] })
                t = h;
        }
        return t switch
        {
            AtomTerm a => (a.Name, 0),
            CompoundTerm cc => (cc.Functor, cc.Args.Length),
            _ => null,
        };
    }

    private static Term Rewrite(Term lhs, Term body, Lexer.SourcePosition pos)
    {
        var cut = new AtomTerm("!") { Position = pos };
        Term pattern;
        Term? guard = null;
        // A leading conjunction is Head + Guard; the head is the first
        // conjunct (the predicate being defined), the rest is the guard.
        if (lhs is CompoundTerm { Functor: ",", Args: [var h, var g] })
        {
            pattern = h;
            guard = g;
        }
        else
        {
            pattern = lhs;
        }

        Term head, newBody;
        if (pattern is CompoundTerm pc)
        {
            // Fresh head variables receive the CALLER's arguments untouched;
            // '$ssu_match' is where single-sidedness is decided.
            var vars = new Term[pc.Args.Length];
            for (int i = 0; i < vars.Length; i++)
                vars[i] = new VarTerm("$SSU" + i) { Position = pos };
            head = new CompoundTerm(pc.Functor, vars) { Position = pos };
            Term match = new CompoundTerm("$ssu_match", new[] { pattern, head })
            { Position = pos };
            newBody = guard is null
                ? Conj(match, Conj(cut, body, pos), pos)
                : Conj(match, Conj(guard, Conj(cut, body, pos), pos), pos);
        }
        else
        {
            // Atom head — nothing to match; the commit (and guard) remain.
            head = pattern;
            newBody = guard is null
                ? Conj(cut, body, pos)
                : Conj(guard, Conj(cut, body, pos), pos);
        }
        return new CompoundTerm(":-", new[] { head, newBody }) { Position = pos };
    }

    private static Clause NoMatchTrailer(string name, int arity, Lexer.SourcePosition pos)
    {
        Term goal;
        if (arity == 0)
        {
            goal = new AtomTerm(name) { Position = pos };
        }
        else
        {
            var vars = new Term[arity];
            for (int i = 0; i < arity; i++)
                vars[i] = new VarTerm("$SSU" + i) { Position = pos };
            goal = new CompoundTerm(name, vars) { Position = pos };
        }
        Term body = new CompoundTerm("$ssu_no_match", new[] { goal }) { Position = pos };
        return Clause.From(new CompoundTerm(":-", new[] { goal, body }) { Position = pos });
    }

    private static Term Conj(Term a, Term b, Lexer.SourcePosition pos) =>
        new CompoundTerm(",", new[] { a, b }) { Position = pos };
}

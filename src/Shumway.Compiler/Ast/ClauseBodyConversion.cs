namespace Shumway.Compiler.Ast;

/// <summary>
/// ISO 13211-1 §7.6.2 — converting a term to the body of a clause. A clause
/// enters the database in its CONVERTED form, so a variable in goal position
/// is stored as <c>call(V)</c> and <c>clause/2</c> hands that back.
///
/// <para>The conversion descends the control skeleton <c>','</c>, <c>';'</c>
/// and <c>'-&gt;'</c> only, which is what §7.6.2 defines and what GNU Prolog —
/// the reference this engine follows — does. (SWI additionally converts
/// inside <c>\+</c> and <c>*-&gt;</c>; those are outside the clause.)</para>
/// </summary>
public static class ClauseBodyConversion
{
    /// <summary>The converted body, or <paramref name="body"/> itself when
    /// nothing changed (no allocation for the overwhelmingly common case of a
    /// body with no variable goals).</summary>
    public static Term Convert(Term body)
    {
        switch (body)
        {
            case VarTerm:
                return new CompoundTerm("call", new[] { body }) { Position = body.Position };
            case CompoundTerm { Functor: "," or ";" or "->", Args.Length: 2 } c:
            {
                Term a = Convert(c.Args[0]);
                Term b = Convert(c.Args[1]);
                return ReferenceEquals(a, c.Args[0]) && ReferenceEquals(b, c.Args[1])
                    ? body
                    : new CompoundTerm(c.Functor, new[] { a, b }) { Position = c.Position };
            }
            default:
                return body;
        }
    }

    /// <summary>The clause with its body converted. Facts and bodies that need
    /// no conversion are returned unchanged.</summary>
    public static Clause Convert(Clause clause)
    {
        if (clause.Kind != ClauseKind.Rule) return clause;
        if (clause.Term is not CompoundTerm { Functor: ":-", Args.Length: 2 } rule)
            return clause;
        Term converted = Convert(rule.Args[1]);
        if (ReferenceEquals(converted, rule.Args[1])) return clause;
        return new Clause(
            clause.Kind,
            new CompoundTerm(":-", new[] { rule.Args[0], converted }) { Position = rule.Position },
            clause.Position);
    }
}

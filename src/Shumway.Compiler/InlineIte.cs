using Shumway.Compiler.Ast;

namespace Shumway.Compiler;

/// <summary>ADR-025 — shared eligibility test for the INLINE if-then-else /
/// disjunction lowering. When enabled, <c>MetaTransform</c> LEAVES an eligible
/// <c>(C -&gt; T ; E)</c> / <c>(A ; B)</c> intact (no <c>$disj_N</c> helper) and
/// <c>ClauseCompiler</c> emits it in the host clause as
/// <c>get_level; try_me_else ELSE; C; cut; T; jump END; ELSE: trust_me; E</c>.
/// The two sides MUST agree, so the predicate lives here, in one place.
///
/// <para>First cut, deliberately conservative: every part must be a conjunction
/// of PLAIN goals — no cuts (those need the MetaTransform barrier threading), no
/// nested control constructs (they keep the helper path, where MetaTransform's
/// rewrites apply), no variables in goal position (runtime call/1 dispatch does
/// the ISO error checks). Extending eligibility widens the win later without
/// touching the emission scheme.</para></summary>
public static class InlineIte
{
    /// <summary>True if <paramref name="disj"/> is a 2-arm <c>;/2</c> whose parts
    /// (Cond/Then/Else for if-then-else; both arms otherwise) are conjunctions of
    /// plain goals per <see cref="IsPlainConjunction"/>.</summary>
    public static bool IsEligible(CompoundTerm disj)
    {
        if (disj.Functor != ";" || disj.Args.Length != 2) return false;
        if (disj.Args[0] is CompoundTerm { Functor: "->", Args.Length: 2 } ite)
            return IsPlainConjunction(ite.Args[0])
                && IsPlainConjunction(ite.Args[1])
                && IsPlainConjunction(disj.Args[1]);
        return IsPlainConjunction(disj.Args[0])
            && IsPlainConjunction(disj.Args[1]);
    }

    /// <summary>A conjunction tree of plain goals: atoms / compounds that are not
    /// control constructs, cuts, or meta-goals MetaTransform rewrites. A variable
    /// in goal position is NOT plain (needs runtime call/1 error checks).</summary>
    public static bool IsPlainConjunction(Term t) => t switch
    {
        CompoundTerm { Functor: ",", Args.Length: 2 } conj =>
            IsPlainConjunction(conj.Args[0]) && IsPlainConjunction(conj.Args[1]),
        AtomTerm { Name: "!" } => false,   // needs the cut barrier — helper path
        AtomTerm a => !IsControlName(a.Name, 0),   // true/fail are plain builtins
        CompoundTerm c => !IsControlName(c.Functor, c.Args.Length),
        _ => false,   // vars, numbers, strings in goal position → runtime path
    };

    // Control constructs / meta-goals that MetaTransform rewrites (or that carry
    // cut/exception semantics the inline form doesn't model). A part containing
    // any of these keeps the synthesized-helper path.
    private static bool IsControlName(string name, int arity) => (name, arity) switch
    {
        (";", 2) or ("->", 2) or ("*->", 2) => true,
        ("\\+", 1) or ("not", 1) => true,
        ("once", 1) or ("ignore", 1) => true,
        ("findall", 3) or ("findall", 4) or ("bagof", 3) or ("setof", 3) => true,
        ("forall", 2) or ("catch", 3) or ("throw", 1) => true,
        ("call", _) => true,
        ("$get_cut_barrier", 1) or ("$call", 2) => true,
        _ => false,
    };
}

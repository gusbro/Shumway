namespace Shumway.Embedding;

/// <summary>The SWI compatibility shim — SWI system predicates that are not
/// standard/ISO and are provided only on demand: it loads AUTOMATICALLY the first
/// time an SWI-dialect module is loaded, and can also be loaded explicitly with
/// <c>use_module(library(swi))</c> (as SWI itself offers <c>library(sicstus)</c>).
/// A pure ISO program that never touches SWI never sees these predicates.
///
/// <para>The public predicates here are thin wrappers over always-available
/// C# helper builtins (<c>$nb_setarg</c>, <c>$same_term</c>,
/// <c>$copy_term_without_attr_vars</c>): the helpers are internal
/// (<c>$</c>-prefixed) so their global availability does not pollute the standard
/// namespace, while the SWI-named predicates only exist once the shim is
/// loaded.</para></summary>
internal static class SwiShim
{
    /// <summary>The module name a manual <c>use_module(library(swi))</c> resolves
    /// to (and the auto-load consults).</summary>
    public const string LibraryName = "swi";

    public const string Source = """
        % ----- destructive term modification (non-backtrackable) -----
        :- public nb_setarg/3.
        nb_setarg(Arg, Term, Value) :- '$nb_setarg'(Arg, Term, Value).
        :- public nb_linkarg/3.
        nb_linkarg(Arg, Term, Value) :- '$nb_setarg'(Arg, Term, Value).

        % ----- term copying / identity -----
        :- public copy_term_nat/2.
        copy_term_nat(Term, Copy) :- '$copy_term_without_attr_vars'(Term, Copy).
        :- public duplicate_term/2.
        duplicate_term(Term, Copy) :- copy_term(Term, Copy).
        :- public same_term/2.
        same_term(A, B) :- '$same_term'(A, B).

        % ----- arithmetic-function introspection -----
        % We do not support user-defined arithmetic functions, so this reports the
        % built-in evaluable functors only.
        :- public current_arithmetic_function/1.
        current_arithmetic_function(Head) :-
            functor(Head, Name, Arity),
            '$arith_function'(Name, Arity).
        '$arith_function'(+, 2). '$arith_function'(-, 2). '$arith_function'(*, 2).
        '$arith_function'(/, 2). '$arith_function'(//, 2). '$arith_function'(mod, 2).
        '$arith_function'(rem, 2). '$arith_function'(div, 2). '$arith_function'(gcd, 2).
        '$arith_function'(min, 2). '$arith_function'(max, 2). '$arith_function'(**, 2).
        '$arith_function'(^, 2). '$arith_function'(>>, 2). '$arith_function'(<<, 2).
        '$arith_function'(/\, 2). '$arith_function'(\/, 2). '$arith_function'(xor, 2).
        '$arith_function'(atan2, 2). '$arith_function'(atan, 2). '$arith_function'(copysign, 2).
        '$arith_function'(truncate, 1). '$arith_function'(round, 1). '$arith_function'(ceiling, 1).
        '$arith_function'(floor, 1). '$arith_function'(integer, 1). '$arith_function'(float, 1).
        '$arith_function'(float_integer_part, 1). '$arith_function'(float_fractional_part, 1).
        '$arith_function'(abs, 1). '$arith_function'(sign, 1). '$arith_function'(sqrt, 1).
        '$arith_function'(sin, 1). '$arith_function'(cos, 1). '$arith_function'(tan, 1).
        '$arith_function'(asin, 1). '$arith_function'(acos, 1). '$arith_function'(exp, 1).
        '$arith_function'(log, 1). '$arith_function'(-, 1). '$arith_function'(+, 1).
        '$arith_function'(\, 1). '$arith_function'(msb, 1).
        '$arith_function'(pi, 0). '$arith_function'(e, 0). '$arith_function'(epsilon, 0).
        """;
}

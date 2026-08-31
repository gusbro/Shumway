using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// Second round of Logtalk-battery-driven ISO conformance: option-list
/// validation for write_term/read_term/open/close, the clause-reference
/// family (asserta/2, clause/3, erase/1), setup_call_cleanup's
/// first-exception-wins and inner-before-outer cleanup order, and the
/// error-culprit sweep over succ/plus/compare/sub_atom/number_codes/
/// set_prolog_flag.
/// </summary>
public class BatteryRoundTwoConformance
{
    private static void Succeeds(string query)
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(query).Success, $"Query failed: {query}");
    }

    // ---------- setup_call_cleanup/3 ----------

    [Fact]
    public void SetupCallCleanup_FirstBallWins()
    {
        // A cleanup that throws while ANOTHER exception is unwinding must
        // not replace it — nor surface later as a phantom second error.
        Succeeds(
            "catch((setup_call_cleanup(true, (_G = 1 ; _G = 2), throw(second)), "
            + "throw(first)), B, B == first).");
    }

    [Fact]
    public void SetupCallCleanup_InnerCleanupRunsBeforeOuter()
    {
        Succeeds(
            "assertz(scc_log([])), "
            + "setup_call_cleanup(true, "
            + "  setup_call_cleanup(true, (true;true), assertz(scc_mark(inner))), "
            + "  assertz(scc_mark(outer))), !, "
            + "findall(M, scc_mark(M), Ms), Ms == [inner, outer].");
    }

    [Fact]
    public void SetupCallCleanup_CleanupBindingsReachTheCaller()
    {
        // The cut fires the cleanup on the LIVE term, so Y is bound after.
        Succeeds(
            "setup_call_cleanup(true, "
            + "  setup_call_cleanup(true, (X = 1 ; X = 2), true), Y = 3), !, "
            + "X == 1, Y == 3.");
    }

    [Fact]
    public void SetupCallCleanup_ArgumentChecks()
    {
        Succeeds("catch(setup_call_cleanup(true, true, _), "
            + "error(instantiation_error, _), true).");
        Succeeds("catch(setup_call_cleanup(true, true, 1), "
            + "error(type_error(callable, 1), _), true).");
        Succeeds("setup_call_cleanup(X = true, true, X).");
    }

    // ---------- clause references ----------

    [Fact]
    public void ClauseReferences_AssertClauseErase()
    {
        Succeeds(
            "assertz(cr_a(1), R1), assertz(cr_a(2), R2), R1 \\== R2, "
            + "clause(cr_a(X), B, R1), X == 1, B == true, "
            + "clause(cr_a(2), true, R3), R3 == R2, "
            + "erase(R1), findall(Y, cr_a(Y), L), L == [2].");
    }

    [Fact]
    public void ClauseReferences_ErrorCases()
    {
        Succeeds("catch(assertz(cr_b(1), ref), error(uninstantiation_error(ref), _), true).");
        Succeeds("catch(erase(_), error(instantiation_error, _), true).");
        Succeeds("catch(erase(3.14), error(type_error(_, 3.14), _), true).");
        Succeeds("assertz(cr_c(1), R), erase(R), \\+ clause(cr_c(_), _, R).");
    }

    // ---------- option-list validation ----------

    [Fact]
    public void WriteTermOptions_AreValidated()
    {
        Succeeds("catch(write_term(1, _), error(instantiation_error, _), true).");
        Succeeds("catch(write_term(1, 2), error(type_error(list, 2), _), true).");
        Succeeds("catch(write_term(1, [quoted(true)|foo]), "
            + "error(type_error(list, _), _), true).");
        Succeeds("catch(write_term(1, [foo]), error(domain_error(write_option, foo), _), true).");
        Succeeds("catch(write_term(1, [quoted(fail)]), "
            + "error(domain_error(write_option, quoted(fail)), _), true).");
    }

    [Fact]
    public void WriteTerm_MaxDepthElidesDeepTerms()
    {
        Succeeds("with_output_to(atom(A), write_term(a(b(c(d(e)))), [max_depth(3)])), "
            + "A == 'a(b(c(...)))'.");
        Succeeds("with_output_to(atom(A), write_term([1,2,3,4,5], [max_depth(3)])), "
            + "A == '[1,2,3|...]'.");
        Succeeds("with_output_to(atom(A), write_term(1, [max_depth(0)])), A == '1'.");
        Succeeds("catch(write_term(1, [max_depth(foo)]), "
            + "error(domain_error(write_option, max_depth(foo)), _), true).");
    }

    [Fact]
    public void ReadTermOptions_AreValidated()
    {
        Succeeds("catch(read_term(_, [foo]), error(domain_error(read_option, foo), _), true).");
        // Cor.3 (Neumerkel variable_names cases 47-49): a recognised OUTPUT
        // option's value unifies AFTER the read — a mismatch fails, it is
        // never a domain_error. Pinned in VariableNamesConformance
        // (V47_48, V69_70); read_term_from_atom/3 is compat and ignores
        // these options, so it cannot carry the pin.
        Succeeds("catch(read_term(_, bar), error(type_error(list, bar), _), true).");
    }

    [Fact]
    public void CloseOptions_AndOpenErrors_AreIso()
    {
        Succeeds("catch(open(f, red, _), error(domain_error(io_mode, red), _), true).");
        Succeeds("catch(open(foo(1,2), read, _), "
            + "error(domain_error(source_sink, foo(1,2)), _), true).");
        Succeeds("catch(open(f, read, bar), error(uninstantiation_error(bar), _), true).");
        Succeeds("catch(open(f, read, _, [bar]), "
            + "error(domain_error(stream_option, bar), _), true).");
    }

    // ---------- stream properties ----------

    [Fact]
    public void StreamProperty_ReportsTypeRepositionEofAction()
    {
        Succeeds("current_input(S), stream_property(S, type(text)), "
            + "stream_property(S, mode(read)), stream_property(S, eof_action(reset)).");
        Succeeds("current_output(S), stream_property(S, mode(append)), "
            + "stream_property(S, type(text)).");
        Succeeds("catch(stream_property(foo, _), error(domain_error(stream, foo), _), true).");
        Succeeds("catch(stream_property(_, foo), "
            + "error(domain_error(stream_property, foo), _), true).");
    }

    // ---------- error culprits ----------

    [Fact]
    public void ErrorCulprits_AreTheOffendingValues()
    {
        Succeeds("catch(succ(a, _), error(type_error(integer, a), _), true).");
        Succeeds("catch(succ(-1, _), error(domain_error(not_less_than_zero, -1), _), true).");
        Succeeds("catch(plus(a, 1, _), error(type_error(integer, a), _), true).");
        Succeeds("catch(compare(1, a, b), error(type_error(atom, 1), _), true).");
        Succeeds("catch(compare(>=, a, b), error(domain_error(order, >=), _), true).");
        Succeeds("catch(sub_atom(f(a), _, _, _, _), error(type_error(atom, f(a)), _), true).");
        Succeeds("catch(sub_atom(abc, a, _, _, _), error(type_error(integer, a), _), true).");
        Succeeds("catch(sub_atom(abc, -2, _, _, _), "
            + "error(domain_error(not_less_than_zero, -2), _), true).");
        Succeeds("catch(number_codes(_, [0'4, a]), error(type_error(integer, a), _), true).");
        Succeeds("catch(set_prolog_flag(5, x), error(type_error(atom, 5), _), true).");
        Succeeds("catch(set_prolog_flag(unknown, foo), "
            + "error(domain_error(flag_value, unknown+foo), _), true).");
        Succeeds("catch(set_prolog_flag(max_arity, 5), "
            + "error(permission_error(modify, flag, max_arity), _), true).");
    }

    // ---------- term inspection ----------

    [Fact]
    public void Arg_ChecksTheTermFirst()
    {
        // §8.5.2.3, verified against GNU: the TERM decides first — a
        // non-compound is type_error(compound, T) whatever N is — then N's
        // type, then its sign.
        Succeeds("catch(arg(0, atom, _), error(type_error(compound, atom), _), true).");
        Succeeds("catch(arg(1, 3, _), error(type_error(compound, 3), _), true).");
        Succeeds("catch(arg(-3, foo(a), _), "
            + "error(domain_error(not_less_than_zero, -3), _), true).");
        Succeeds("catch(arg(a, foo(a), _), error(type_error(integer, a), _), true).");
        Succeeds("catch(arg(_, _, _), error(instantiation_error, _), true).");
        Succeeds("arg(2, foo(a, b), b).");
    }

    [Fact]
    public void Univ_ChecksInstantiationBeforeShape()
    {
        Succeeds("catch(_ =.. _, error(instantiation_error, _), true).");
        Succeeds("catch(_ =.. [foo, a|_], error(instantiation_error, _), true).");
        Succeeds("catch(_ =.. [foo|bar], error(type_error(list, [foo|bar]), _), true).");
        Succeeds("catch(_ =.. [_, bar], error(instantiation_error, _), true).");
        Succeeds("catch(_ =.. [3, bar], error(type_error(atom, 3), _), true).");
    }

    [Fact]
    public void CurrentPredicate_IsUserPredicatesOnly()
    {
        // §8.8.2: builtins and library predicates are NOT current
        // predicates (GNU agrees); predicate_property/2 answers for them.
        Succeeds("\\+ current_predicate(current_predicate/1).");
        Succeeds("\\+ current_predicate(atom/1).");
        Succeeds("predicate_property(atom(_), built_in).");
        // Indicator shape is validated.
        Succeeds("catch(current_predicate(0/dog), "
            + "error(type_error(predicate_indicator, 0/dog), _), true).");
        Succeeds("catch(current_predicate(f/f), "
            + "error(type_error(predicate_indicator, f/f), _), true).");
    }

    [Fact]
    public void PredicateProperty_ReportsMetaTemplates()
    {
        // The meta-templates a portable program (and Logtalk's compiler)
        // reads to decide which arguments are goals.
        Succeeds("predicate_property(findall(_,_,_), meta_predicate(findall(*, 0, *))).");
        Succeeds("predicate_property(call(_,_,_), meta_predicate(call(2, *, *))).");
        Succeeds("predicate_property((_,_), meta_predicate(T)), T == (0,0).");
        Succeeds("predicate_property((_;_), meta_predicate(T)), T == (0;0).");
        Succeeds("predicate_property(catch(_,_,_), meta_predicate(catch(0, *, 0))).");
    }

    [Fact]
    public void Functor_ConstructModeChecks()
    {
        Succeeds("catch(functor(_, foo, _), error(instantiation_error, _), true).");
        Succeeds("catch(functor(_, foo, a), error(type_error(integer, a), _), true).");
        Succeeds("catch(functor(_, 1.5, 1), error(type_error(atom, 1.5), _), true).");
        Succeeds("catch(functor(_, foo(a), 1), error(type_error(atomic, foo(a)), _), true).");
        Succeeds("catch(functor(_, foo, -1), "
            + "error(domain_error(not_less_than_zero, -1), _), true).");
        Succeeds("catch(functor(_, foo, 300), "
            + "error(representation_error(max_arity), _), true).");
        Succeeds("functor(T, foo, 2), T = foo(_, _).");
    }

    [Fact]
    public void SortFamily_ValidatesTheListArgument()
    {
        // §8.4.3.3: the list argument is checked as a whole, with the
        // WHOLE argument as the culprit, before any element is inspected.
        Succeeds("catch(sort(3, _), error(type_error(list, 3), _), true).");
        Succeeds("catch(sort([a|b], _), error(type_error(list, [a|b]), _), true).");
        Succeeds("catch(msort([a,b|c], _), error(type_error(list, [a,b|c]), _), true).");
        Succeeds("catch(keysort([1-a|b], _), error(type_error(list, [1-a|b]), _), true).");
        Succeeds("catch(keysort([_|_], _), error(instantiation_error, _), true).");
        Succeeds("catch(keysort([a], _), error(type_error(pair, a), _), true).");
        Succeeds("sort([b,a,c], L), L == [a,b,c].");
        Succeeds("keysort([b-1, a-2], L), L == [a-2, b-1].");
    }

    [Fact]
    public void PartialStringsRenderInsideLists()
    {
        // Regression: a PSTR element read straight out of a list arrives
        // already dereferenced, and the renderer's index-based entry point
        // crashed on it (an out-of-bounds heap index) — msort/2 over a list
        // holding a string was enough to hit it.
        Succeeds("msort([f(x), a, 1, \"s\"], L), "
            + "with_output_to(atom(A), write(L)), atom_length(A, _).");
    }

    [Fact]
    public void AtomTextConversions_CheckBoundListsBothWays()
    {
        // With BOTH arguments bound the list is still type-checked
        // (§8.16.4/8.16.5) — but only when it is fully ground: a partial
        // list, or one holding unbound elements, is the generate
        // direction and must unify.
        Succeeds("catch(atom_codes(abc, [a,b,c]), "
            + "error(type_error(integer, a), _), true).");
        Succeeds("catch(atom_codes('ABC', [66|67]), "
            + "error(type_error(list, [66|67]), _), true).");
        Succeeds("catch(atom_chars(abc, ['A'|'B']), "
            + "error(type_error(list, ['A'|'B']), _), true).");
        Succeeds("catch(atom_codes(f(a), _), error(type_error(atom, f(a)), _), true).");
        // Generate directions still work.
        Succeeds("atom_codes(abc, [0'a|T]), T == [0'b, 0'c].");
        Succeeds("atom_codes(A, [0'a]), atom_codes(A, [Y]), Y == 0'a.");
        Succeeds("atom_chars(abc, L), L == [a,b,c].");
    }

    [Fact]
    public void UnboundedIntegers_ReachSuccPlusAndFloatDomains()
    {
        // succ/2 and plus/3 relate BIGNUMS, not just machine integers.
        Succeeds("succ(123456789012345678901234567890, N), "
            + "N =:= 123456789012345678901234567891.");
        Succeeds("succ(N, 123456789012345678901234567891), "
            + "N =:= 123456789012345678901234567890.");
        Succeeds("plus(123456789012345678901234567890, "
            + "987654321098765432109876543210, S), "
            + "S =:= 1111111110111111111011111111100.");
        // A float-domain function whose integer argument exceeds the float
        // range is evaluation_error(float_overflow), not silent infinity.
        Succeeds("catch(_ is sqrt(7^7^7), "
            + "error(evaluation_error(float_overflow), _), true).");
        Succeeds("catch(_ is log(7^7^7), "
            + "error(evaluation_error(float_overflow), _), true).");
        Succeeds("X is sqrt(16), X =:= 4.0.");
    }

    [Fact]
    public void AtomicListConcat_ChecksItsArguments()
    {
        // (put_byte's byte culprits need a binary stream and live in the
        // battery's own put_byte_2 tester, now 21/21.)
        Succeeds("atomic_list_concat([a, 42, c], '_', A), A == 'a_42_c'.");
        Succeeds("atomic_list_concat(L, '_', 'a_b_c'), L == [a, b, c].");
        Succeeds("catch(atomic_list_concat([_, bar], '_', _), "
            + "error(instantiation_error, _), true).");
        Succeeds("catch(atomic_list_concat([foo, bar|_], '_', _), "
            + "error(instantiation_error, _), true).");
        Succeeds("catch(atomic_list_concat([a(1)], '_', _), "
            + "error(type_error(atomic, a(1)), _), true).");
        Succeeds("catch(atomic_list_concat([foo, bar], _, _), "
            + "error(instantiation_error, _), true).");
    }

    [Fact]
    public void AtomBuiltins_CheckEveryBoundArgument()
    {
        Succeeds("catch(atom_length(1.23, _), error(type_error(atom, 1.23), _), true).");
        Succeeds("catch(atom_length(abc, '4'), error(type_error(integer, '4'), _), true).");
        Succeeds("catch(atom_length(abc, -4), "
            + "error(domain_error(not_less_than_zero, -4), _), true).");
        Succeeds("catch(atom_concat(a, f(a), _), error(type_error(atom, f(a)), _), true).");
        Succeeds("catch(atom_concat(foo, 42, _), error(type_error(atom, 42), _), true).");
        Succeeds("catch(char_code(a, x), error(type_error(integer, x), _), true).");
        Succeeds("catch(between(a, 2, _), error(type_error(integer, a), _), true).");
        Succeeds("atom_length(hello, 5), atom_concat(he, llo, hello), between(1, 3, 2).");
    }

    [Fact]
    public void CurrentOp_ValidatesItsArguments()
    {
        // §8.14.4.3 — a bound argument is checked before the enumeration.
        Succeeds("catch(current_op(1201, xfx, _), "
            + "error(domain_error(operator_priority, 1201), _), true).");
        Succeeds("catch(current_op(_, yfy, _), "
            + "error(domain_error(operator_specifier, yfy), _), true).");
        Succeeds("catch(current_op(_, 0, _), error(type_error(atom, 0), _), true).");
        Succeeds("catch(current_op(_, _, 5), error(type_error(atom, 5), _), true).");
        Succeeds("catch(current_op(a, _, _), error(type_error(integer, a), _), true).");
        Succeeds("current_op(P, xfx, =), P == 700.");
    }

    // ---------- standard order of terms ----------

    [Fact]
    public void StandardOrder_AllFloatsPrecedeAllIntegers()
    {
        // ISO §7.2.1: the TYPE decides between a float and an integer,
        // never the value — verified identical to GNU Prolog.
        Succeeds("compare(<, 1.1, 1).");
        Succeeds("compare(>, 1, 1.1).");
        Succeeds("compare(<, 1.0, 1).");
        Succeeds("1.0 @< 1.");
        Succeeds("msort([3, 1.5, 2, 0.5, 1], L), L == [0.5, 1.5, 1, 2, 3].");
        Succeeds("sort([b, 2, 1.0, a, 1], S), S == [1.0, 1, 2, a, b].");
        // Within one type the value still decides.
        Succeeds("compare(<, 1.5, 2.5).");
        Succeeds("compare(<, 1, 2).");
    }

    // ---------- format/2,3 ----------

    [Fact]
    public void Format_ColumnAlignment()
    {
        // ~t marks fill points, ~N| an absolute column stop, ~N+ a stop N
        // columns past where the segment began. No fill point means the
        // padding goes on the right (left-aligned).
        Succeeds("with_output_to(atom(A), format('~w~t~20|~w', [left, right])), "
            + "A == 'left                right'.");
        Succeeds("with_output_to(atom(A), format('~t~w~20|', [right_aligned])), "
            + "A == '       right_aligned'.");
        Succeeds("with_output_to(atom(A), format('~w~t~10+~w', [col1, col2])), "
            + "A == 'col1      col2'.");
        Succeeds("with_output_to(atom(A), format('~`-t~10|', [])), A == '----------'.");
    }

    [Fact]
    public void Format_NumericDirectivesTakeExpressions()
    {
        // ~d / ~D / ~r / ~e evaluate their argument (GNU, SWI, SICStus).
        Succeeds("with_output_to(atom(A), format('~d', [1+1])), A == '2'.");
        Succeeds("catch(format('~d', [foo(bar)]), "
            + "error(type_error(evaluable, foo/1), _), true).");
        Succeeds("catch(format('~d', [1.5]), error(type_error(integer, 1.5), _), true).");
        Succeeds("catch(format('~d', [_]), error(instantiation_error, _), true).");
        Succeeds("with_output_to(atom(A), format('~2d', [1234])), A == '12.34'.");
        Succeeds("with_output_to(atom(A), format('~8r', [64])), A == '100'.");
        Succeeds("catch(format('~0r', [16]), error(domain_error(radix, _), _), true).");
    }

    [Fact]
    public void Format_ArgumentChecksAndNewlines()
    {
        Succeeds("catch(format('~a', [42]), error(type_error(atom, 42), _), true).");
        Succeeds("catch(format('~s', [42]), error(type_error(_, 42), _), true).");
        Succeeds("catch(format('~s', [[65,66|_]]), error(instantiation_error, _), true).");
        // Every argument must be consumed.
        Succeeds("catch(format('abc', [def]), error(domain_error(_, _), _), true).");
        // ~n writes LF, and ~Nn writes N of them.
        Succeeds("with_output_to(atom(A), format('a~nb', [])), atom_length(A, 3).");
        Succeeds("with_output_to(atom(A), format('~2n', [])), atom_length(A, 2).");
        // ~w honours numbervars, like write/1.
        Succeeds("T = a(_), numbervars(T, 0, _), "
            + "with_output_to(atom(A), format('~w', [T])), A == 'a(A)'.");
        // ~Ns truncates the code list.
        Succeeds("with_output_to(atom(A), format('~0s', [[65,66,67]])), A == ''.");
        Succeeds("with_output_to(atom(A), format('~2s', [[65,66,67]])), A == 'AB'.");
    }

    // ---------- call_nth/2 ----------

    [Fact]
    public void CallNth_CountsAndCommits()
    {
        Succeeds("findall(X-N, call_nth(member(X, [a,b,c]), N), L), "
            + "L == [a-1, b-2, c-3].");
        Succeeds("call_nth(member(X, [a,b,c]), 2), X == b.");
        Succeeds("\\+ call_nth(member(_, [a]), 2).");
        Succeeds("catch(call_nth(_, 1), error(instantiation_error, _), true).");
        Succeeds("catch(call_nth(1, 1), error(type_error(callable, 1), _), true).");
        Succeeds("catch(call_nth(member(_, [a]), -1), "
            + "error(domain_error(not_less_than_zero, -1), _), true).");
    }
}

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
        Succeeds("catch(read_term(_, [variables(a)]), "
            + "error(domain_error(read_option, variables(a)), _), true).");
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

using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Single-threaded compat shims (SWI/SICStus surface):
/// <c>with_mutex/2</c> + mutex no-ops, message queues as FIFO buffers,
/// <c>must_be/2</c>, <c>print_message/2</c>, and <c>module_property/2</c>.</summary>
public sealed class ShimPredicatesTests
{
    [Fact]
    public void WithMutex_RunsGoalOnce()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("with_mutex(m, X = 42), X == 42.").Success);
        // Committed to the first solution (once/1 semantics).
        int n = 0;
        foreach (var _ in e.QueryAll("with_mutex(m, member(X, [1,2,3])).")) n++;
        Assert.Equal(1, n);
    }

    [Fact]
    public void MutexNoOps_Succeed()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "mutex_create(M), mutex_lock(M), mutex_unlock(M), mutex_destroy(M).").Success);
    }

    [Fact]
    public void MessageQueue_IsFifo()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "message_queue_create(Q), "
            + "thread_send_message(Q, a), thread_send_message(Q, b), "
            + "thread_get_message(Q, X), thread_get_message(Q, Y), "
            + "X == a, Y == b.").Success);
    }

    [Fact]
    public void MessageQueue_PeekLeavesMessage()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "message_queue_create(Q), thread_send_message(Q, hello), "
            + "thread_peek_message(Q, hello), thread_get_message(Q, hello).").Success);
    }

    [Fact]
    public void MessageQueue_EmptyGetFails()
    {
        var e = new PrologEngine();
        Assert.False(e.Query(
            "message_queue_create(Q), thread_get_message(Q, _).").Success);
    }

    [Fact]
    public void MessageQueue_FreshIdsAreDistinct()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "message_queue_create(Q1), message_queue_create(Q2), Q1 \\== Q2.").Success);
    }

    [Fact]
    public void MustBe_AcceptsMatchingType()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("must_be(integer, 5).").Success);
        Assert.True(e.Query("must_be(atom, foo).").Success);
        Assert.True(e.Query("must_be(list, [1,2,3]).").Success);
        Assert.True(e.Query("must_be(positive_integer, 3).").Success);
        Assert.True(e.Query("must_be(boolean, true).").Success);
        Assert.True(e.Query("must_be(var, _).").Success);
    }

    [Fact]
    public void MustBe_ThrowsTypeError()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(integer, foo), error(type_error(integer, foo), _), true).").Success);
    }

    [Fact]
    public void MustBe_ThrowsInstantiationError()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(integer, _), error(instantiation_error, _), true).").Success);
    }

    [Fact]
    public void MustBe_DomainNameRaisesADomainError()
    {
        // Issue #40 (UWN): not_less_than_zero is an element of ValidDomain
        // (7.12.2 c), not a type, so calling the value's type wrong was.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(can_be(not_less_than_zero, -1), E, true), "
          + "E == error(domain_error(not_less_than_zero, -1), can_be/2).").Success);
        Assert.True(e.Query(
            "catch(must_be(not_less_than_zero, -1), E, true), "
          + "E == error(domain_error(not_less_than_zero, -1), must_be/2).").Success);
        Assert.True(e.Query("must_be(not_less_than_zero, 0).").Success);
    }

    [Fact]
    public void MustBe_TheTypeIsCheckedBeforeTheDomain()
    {
        // The order 7.12.2 itself takes: atom_length(_, foo) is a type error,
        // atom_length(_, -1) a domain error. A domain is a subset of a type,
        // so a culprit that is not even the right type never reaches it.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(not_less_than_zero, foo), error(type_error(integer, foo), _), true).")
            .Success);
        Assert.True(e.Query(
            "catch(must_be(operator_priority, foo), error(type_error(integer, foo), _), true).")
            .Success);
        Assert.True(e.Query(
            "catch(must_be(not_empty_list, foo), error(type_error(list, foo), _), true).")
            .Success);
    }

    [Fact]
    public void MustBe_TheOtherDomainNames()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(operator_priority, 2000), "
          + "error(domain_error(operator_priority, 2000), _), true), "
          + "catch(must_be(operator_specifier, zzz), "
          + "error(domain_error(operator_specifier, zzz), _), true), "
          + "catch(must_be(io_mode, sideways), error(domain_error(io_mode, sideways), _), true), "
          + "catch(must_be(not_empty_list, []), error(domain_error(not_empty_list, []), _), true), "
          + "catch(must_be(oneof([a,b]), c), error(domain_error(oneof([a,b]), c), _), true).")
            .Success);
        Assert.True(e.Query(
            "must_be(operator_priority, 1200), must_be(operator_specifier, xfx), "
          + "must_be(io_mode, append), must_be(not_empty_list, [q]), "
          + "must_be(oneof([a,b]), b).").Success);
    }

    [Fact]
    public void MustBe_NonnegIsTheShortNameForTheIsoDomain()
    {
        // The check gets the name we would pick; the error keeps the one
        // 7.12.2 fixes, since that is the term a caller matches on.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(nonneg, -1), error(domain_error(not_less_than_zero, -1), _), true).")
            .Success);
    }

    [Fact]
    public void MustBe_ByteAndCharacterStayTypeErrors()
    {
        // Both are elements of ValidType (7.12.2 b) even though each is a
        // range: put_byte(S, 300) is the standard's own type_error(byte, 300).
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(byte, 300), error(type_error(byte, 300), _), true), "
          + "catch(must_be(character, ab), error(type_error(character, ab), _), true), "
          + "catch(must_be(predicate_indicator, foo), "
          + "error(type_error(predicate_indicator, foo), _), true).").Success);
        Assert.True(e.Query(
            "must_be(byte, 255), must_be(in_byte, -1), must_be(character, a), "
          + "must_be(in_character, end_of_file), must_be(predicate_indicator, foo/2).").Success);
    }

    [Fact]
    public void MustBe_AnUnknownNameIsNotAVerdictOnTheValue()
    {
        // Reporting type_error(no_such_check, x) says x is the wrong thing.
        // What is wrong is the check that was asked for — and it is wrong
        // whatever the value looks like, so it outranks even the unbound
        // value that would otherwise pass can_be or raise instantiation.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(no_such_check, x), "
          + "error(domain_error(must_be_type, no_such_check), _), true), "
          + "catch(must_be(no_such_check, _), "
          + "error(domain_error(must_be_type, no_such_check), _), true), "
          + "catch(can_be(no_such_check, _), "
          + "error(domain_error(must_be_type, no_such_check), _), true).").Success);
        Assert.True(e.Query(
            "catch(must_be(_, x), error(instantiation_error, _), true), "
          + "catch(can_be(_, x), error(instantiation_error, _), true).").Success);
    }

    [Fact]
    public void MustBe_APartialListIsInsufficientNotWrong()
    {
        // The reading every §8 builtin takes (atom_chars, number_codes, ...):
        // an open tail is instantiation_error, but only while the elements
        // already there are compatible — a wrong element is refutable now.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(list, [a|_]), error(instantiation_error, _), true), "
          + "catch(must_be(not_empty_list, [a|_]), error(instantiation_error, _), true), "
          + "catch(must_be(chars, [a|_]), error(instantiation_error, _), true), "
          + "catch(must_be(codes, [0'a|_]), error(instantiation_error, _), true).").Success);
        Assert.True(e.Query(
            "catch(must_be(chars, [1|_]), error(type_error(chars, [1|_]), _), true), "
          + "catch(must_be(list, [a|b]), error(type_error(list, [a|b]), _), true).").Success);
    }

    [Fact]
    public void MustBe_VarWantedGetsUninstantiationError()
    {
        // "Shall be a variable" has its own error term: no term IS the type
        // "variable", so type_error(var, foo) misdescribes the failure.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(must_be(var, foo), error(uninstantiation_error(foo), _), true).").Success);
        Assert.True(e.Query("must_be(var, _).").Success);
    }

    [Fact]
    public void CanBe_AdmitsWhatCouldStillBecomeAList()
    {
        // The whole difference from must_be: a term not yet refutable passes.
        var e = new PrologEngine();
        Assert.True(e.Query("can_be(list, [a|_]).").Success);
        Assert.True(e.Query("can_be(integer, _), can_be(not_less_than_zero, _).").Success);
        Assert.True(e.Query("can_be(chars, [a,b|_]), can_be(codes, [0'a|_]).").Success);
        // What can never become one still raises, and a proper list still passes.
        Assert.True(e.Query(
            "catch(can_be(list, [a|b]), error(type_error(list, [a|b]), _), true).").Success);
        Assert.True(e.Query("can_be(list, [a,b]).").Success);
        // A cyclic spine has no open tail to reach: refused, not walked.
        Assert.True(e.Query(
            "X = [a|X], catch(can_be(list, X), error(type_error(list, _), _), true).").Success);
    }

    [Fact]
    public void SkipMaxList_StopsAtAnOpenTail()
    {
        // It bound the open tail to a fresh cons and walked into the list it
        // had just made. Its own contract is to stop at a non-cons tail.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "'$skip_max_list'(N, _, [a,b|T], Tail), N == 2, Tail == T, var(Tail).").Success);
    }

    [Fact]
    public void PrintMessage_SilentSucceeds()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("print_message(silent, anything).").Success);
        // Non-silent kinds also succeed (best-effort render to user_error).
        Assert.True(e.Query("print_message(informational, format('~w', [hi])).").Success);
    }

    [Fact]
    public void ModuleProperty_Exports()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mymod, [foo/1, bar/2]).
            foo(1).
            bar(a, b).
            """);
        // exports/1 lists the module's exported indicators.
        Assert.True(e.Query("module_property(mymod, exports(Es)), memberchk(foo/1, Es).").Success);
        Assert.True(e.Query("module_property(mymod, exports(Es)), memberchk(bar/2, Es).").Success);
    }

    [Fact]
    public void ModuleProperty_Class()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mymod, [foo/1]).
            foo(1).
            """);
        Assert.True(e.Query("module_property(mymod, class(library)).").Success);
        Assert.True(e.Query("module_property(user, class(user)).").Success);
    }

    [Fact]
    public void ModuleProperty_UnknownModuleFails()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("module_property(no_such_module, exports(_)).").Success);
    }

    [Fact]
    public void ModuleProperty_EnumeratesModules()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mymod, [foo/1]).
            foo(1).
            """);
        // Unbound module: backtracks over loaded modules, binding the class of
        // each. mymod must appear as a library-class module.
        Assert.True(e.Query("module_property(M, class(library)), M == mymod.").Success);
    }
}

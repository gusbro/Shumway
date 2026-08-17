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

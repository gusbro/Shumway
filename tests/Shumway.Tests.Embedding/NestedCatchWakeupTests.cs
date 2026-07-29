using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// catch/throw interacting with the attributed-variable machinery — the two
/// engine bugs clpz's <c>with_local_attributes</c> idiom surfaced (its
/// all_distinct runs a matching algorithm on scratch attributes, then THROWS
/// to undo them, catching inside the propagation itself):
///
/// <para>1. A throw caught by a catch/3 opened INSIDE a verify_attributes
/// wakeup used to unwind the nested C# dispatch driver itself: the recovery
/// resumed in the OUTER loop and the interrupted unification's continuation
/// was silently lost — the enclosing query "succeeded" having skipped every
/// goal after the unification (Activation.NestedCatchResolver).</para>
///
/// <para>2. Wakeups queued and not yet flushed when a ball unwinds to a catch
/// frame — or queued by the catcher trial-unification itself — recorded heap
/// indices into regions the catch rollback truncates; flushing them later
/// read garbage cells (a phantom functor id crash).</para>
/// </summary>
public class NestedCatchWakeupTests
{
    // Native SWI-style hook (verify_attributes/4: Module, AttrValue, Value,
    // Goals) — no library needed, so the tests run without a search path.
    private const string AttsHookProgram =
        "verify_attributes(m, foo(N), _Value, []) :-\n" +
        "    (   N =:= 3 -> catch((X = t(N), throw(i(X))), i(_), true)\n" +
        "    ;   N =:= 7 -> throw(boom)\n" +
        "    ;   true\n" +
        "    ).\n";

    [Fact]
    public void CaughtThrowInsideHook_KeepsTheCallersContinuation()
    {
        var e = new PrologEngine();
        e.ConsultString(AttsHookProgram +
            "t(D) :- put_attr(V, m, foo(3)), V = a, D = yes.");
        var sol = e.Query("t(D).");
        Assert.True(sol.Success);
        // The bug: the query "succeeded" with D unbound — the throw's C#
        // unwinding destroyed the nested wakeup driver and with it the
        // clause continuation after `V = a`.
        Assert.Equal("yes", Assert.IsType<AtomTerm>(sol["D"]).Name);
    }

    [Fact]
    public void CaughtThrowInsideHook_OtherVarsKeepTheirAttributes()
    {
        var e = new PrologEngine();
        e.ConsultString(AttsHookProgram +
            "t(A) :- put_attr(V, m, foo(3)), put_attr(W, m, foo(1)),\n" +
            "        V = a,\n" +
            "        (   get_attr(W, m, foo(A)) -> true ; A = none ).");
        var sol = e.Query("t(A).");
        Assert.True(sol.Success);
        Assert.Equal(1L, Assert.IsType<IntTerm>(sol["A"]).Value);
    }

    [Fact]
    public void ThrowFromHook_WithPendingWakeups_CaughtOutside_EngineStaysSane()
    {
        // f(V,W) = f(1,2) queues wakeups for BOTH attvars before the flush;
        // V's hook throws (foo(7) → boom) and the catch frame's rollback
        // truncates the heap region W's queued entry points into — without
        // the queue truncation the next flush decoded garbage.
        var e = new PrologEngine();
        e.ConsultString(AttsHookProgram +
            "t(R) :- put_attr(V, m, foo(7)), put_attr(W, m, foo(1)),\n" +
            "        catch(f(V, W) = f(1, 2), boom, R = caught).");
        var sol = e.Query("t(R).");
        Assert.True(sol.Success);
        Assert.Equal("caught", Assert.IsType<AtomTerm>(sol["R"]).Name);
        // And the engine is not corrupted: a normal follow-up query works.
        Assert.True(e.Query("X = f(a), functor(X, N, 1), N == f.").Success);
    }

    [Fact]
    public void CatcherTrialUnification_DoesNotLeakWakeups()
    {
        // The inner catcher f(V, a) trial-unifies V (an attvar) with 1 —
        // queuing a wakeup — then fails on b \= a; the ball falls through to
        // the outer frame. The trial's rollback must drop that queued wakeup
        // with the rest of its speculative state.
        var e = new PrologEngine();
        e.ConsultString(AttsHookProgram +
            "t(R) :- put_attr(V, m, foo(1)),\n" +
            "        catch(catch(throw(f(1, b)), f(V, a), R = wrong),\n" +
            "              f(_, b), R = outer).");
        var sol = e.Query("t(R).");
        Assert.True(sol.Success);
        Assert.Equal("outer", Assert.IsType<AtomTerm>(sol["R"]).Name);
        Assert.True(e.Query("X = g(b), functor(X, N, 1), N == g.").Success);
    }

    [Fact]
    public void PlainHookPaths_Unchanged()
    {
        var e = new PrologEngine();
        e.ConsultString(AttsHookProgram +
            "t1(D) :- put_attr(V, m, foo(0)), V = a, D = yes.\n" +
            "t2(D) :- put_attr(V, m, foo(1)), V = a, D = yes.");
        Assert.Equal("yes", Assert.IsType<AtomTerm>(e.Query("t1(D).")["D"]).Name);
        Assert.Equal("yes", Assert.IsType<AtomTerm>(e.Query("t2(D).")["D"]).Name);
    }
}

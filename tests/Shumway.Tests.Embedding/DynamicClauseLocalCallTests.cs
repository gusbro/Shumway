using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Regression coverage for the case where a dynamic predicate's
/// clause body calls a user-module-local predicate. Pre-fix the
/// dynamic-clause rewrite ran with an empty <c>locals</c> set, so
/// the call site stayed bare while the user module's local
/// predicate was mangled to <c>user$name/N</c> by the rest of the
/// rewrite. Result: <c>existence_error/2</c> on the body call.
/// </summary>
public class DynamicClauseLocalCallTests
{
    [Fact]
    public void DynamicClauseBody_CallsLocalPredicate_Resolves()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic main/0.
            main :- helper.
            helper :- true.
            """);
        Assert.True(e.Query("main.").Success);
    }

    [Fact]
    public void DynamicClauseBody_CallsAnotherDynamic_Resolves()
    {
        // Sanity check: dynamic→dynamic still works (it did before
        // too — this is the "bare body call, bare target" case).
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic main/0.
            :- dynamic helper/0.
            main :- helper.
            helper.
            """);
        Assert.True(e.Query("main.").Success);
    }

    [Fact]
    public void DynamicClauseBody_BuiltinCalls_StillWork()
    {
        // A dynamic clause body that calls builtins.
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic compute/1.
            compute(R) :- X is 1 + 2, R = X.
            """);
        var sol = e.Query("compute(R).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void RuntimeAssertz_BodyCallsLocalPredicate_Resolves()
    {
        // assertz at runtime of a clause whose body calls a static
        // user-module predicate. Same mangling concern.
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic action/0.
            do_work :- true.
            """);
        Assert.True(e.Query("assertz((action :- do_work)).").Success);
        Assert.True(e.Query("action.").Success);
    }

    [Fact]
    public void DynamicClauseWithCatchAndBlintShape_Resolves()
    {
        // Approximates Blint.pl's main/0 shape: dynamic main, body
        // wrapped in catch/3, body invokes a local predicate.
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic main/0.
            main :- catch(version(V), _E, V = unknown), V = '1.0'.
            version('1.0').
            """);
        Assert.True(e.Query("main.").Success);
    }
}
